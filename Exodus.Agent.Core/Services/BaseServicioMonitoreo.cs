using Exodus.Agent.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nest;

namespace Exodus.Agent.Core.Services;

public abstract class BaseServicioMonitoreo : BackgroundService, IServicioMonitoreo
{
    protected readonly IElasticClient Elastic;
    protected readonly IConfiguration Config;
    protected readonly ILogger Logger;
    protected readonly EstadoEjecucionAgente Estado;
    private readonly SemaphoreSlim Candado = new(1, 1);

    public abstract string NombrePlataforma { get; }

    protected BaseServicioMonitoreo(IElasticClient pElastic, IConfiguration pConfig,
        ILogger pLogger, EstadoEjecucionAgente pEstado)
    {
        Elastic = pElastic;
        Config = pConfig;
        Logger = pLogger;
        Estado = pEstado;
    }

    protected override async Task ExecuteAsync(CancellationToken pCancelacionToken)
    {
        var Intervalo = ObtenerIntervaloRevisionSegundos();

        while (!pCancelacionToken.IsCancellationRequested)
        {
            await EjecutarUnaVezAsync(pCancelacionToken);

            try { await Task.Delay(TimeSpan.FromSeconds(Intervalo), pCancelacionToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task EjecutarUnaVezAsync(CancellationToken pCancelacionToken)
    {
        await Candado.WaitAsync(pCancelacionToken);
        try
        {
            Logger.LogInformation("Iniciando ciclo de monitoreo {Plataforma}...", NombrePlataforma);
            await ProcesarCicloAsync(pCancelacionToken);
            await EnviarLatidoAsync(pCancelacionToken);
            Estado.MarcarEjecucionExitosa();
            Logger.LogInformation("Ciclo de monitoreo {Plataforma} completado.", NombrePlataforma);
        }
        catch (Exception Ex)
        {
            Logger.LogError(Ex, "Error durante el ciclo de monitoreo {Plataforma}.", NombrePlataforma);
        }
        finally { Candado.Release(); }
    }

    protected abstract Task ProcesarCicloAsync(CancellationToken pCancelacionToken);
    protected abstract Task<string> ObtenerNombreNodoAsync(CancellationToken pCancelacionToken);

    protected virtual string ObtenerSistemaOperativo()
        => DetectorSistemaOperativo.ObtenerSistemaOperativo();

    protected async Task<EstadoServicio?> ObtenerDocumentoExistenteAsync(string pId,
        CancellationToken pCancelacionToken)
    {
        var Respuesta = await Elastic.GetAsync<EstadoServicio>(pId,
            g => g.Index(Config["Elastic:ServicesIndex"]), pCancelacionToken);
        return Respuesta.Found ? Respuesta.Source : null;
    }

    protected async Task IndexarEstadoAsync(EstadoServicio pEstado,
        CancellationToken pCancelacionToken)
    {
        await Elastic.IndexAsync(pEstado, i => i
            .Index(Config["Elastic:ServicesIndex"])
            .Id(pEstado.IdServicio), pCancelacionToken);
    }

    /// <summary>
    /// Agrega un HealthCheck al historial y sincroniza los campos top-level de
    /// CpuPorcentaje y MemoriaPorcentaje con los valores max del ciclo actual.
    /// </summary>
    protected void AgregarHealthCheckHistorico(EstadoServicio pDocumento, DateTime pTiempoActual,
        int pReplicasRunning, int pReplicasDeseadas, List<ResultadoPruebaUrl> pPruebasUrls,
        MetricasRecursos? pRecursos = null)
    {
        pDocumento.HealthChecks ??= new List<HealthCheck>();

        pDocumento.HealthChecks.Add(new HealthCheck
        {
            TsUtc = pTiempoActual,
            Salud = pDocumento.Salud,
            UrlExitosa = pDocumento.UrlExitosa,
            Detalle = pDocumento.SaludDetalle,
            Estado = pDocumento.Estado,
            HostsRunning = pDocumento.NombreHost,
            ReplicasRunning = pReplicasRunning,
            ReplicasDeseadas = pReplicasDeseadas,
            SaludInterna = pDocumento.SaludInterna,
            SaludDetalleInterna = pDocumento.SaludDetalleInterna,
            UrlExitosaInterna = pDocumento.UrlExitosaInterna,
            SaludExterna = pDocumento.SaludExterna,
            SaludDetalleExterna = pDocumento.SaludDetalleExterna,
            UrlExitosaExterna = pDocumento.UrlExitosaExterna,
            PruebasUrls = pPruebasUrls,
            Recursos = pRecursos
        });

        // Top-level refleja el peor-caso del ciclo que acaba de cerrar.
        pDocumento.CpuPorcentaje = pRecursos?.CpuPorcentajeMax;
        pDocumento.MemoriaPorcentaje = pRecursos?.MemoriaPorcentajeMax;

        AplicarRetencion(pDocumento, pTiempoActual);
    }

    private void AplicarRetencion(EstadoServicio pDocumento, DateTime pTiempoActual)
    {
        var (Retencion, MaximoChecks) = ObtenerPoliticaRetencion();
        var LimiteFecha = pTiempoActual - Retencion;

        pDocumento.HealthChecks = pDocumento.HealthChecks
            .Where(h => h.TsUtc >= LimiteFecha)
            .OrderBy(h => h.TsUtc)
            .ToList();

        if (pDocumento.HealthChecks.Count > MaximoChecks)
        {
            pDocumento.HealthChecks = pDocumento.HealthChecks
                .Skip(pDocumento.HealthChecks.Count - MaximoChecks)
                .ToList();
        }
    }

    private async Task EnviarLatidoAsync(CancellationToken pCancelacionToken)
    {
        var Nodo = await ObtenerNombreNodoAsync(pCancelacionToken);
        var BaseAgentId = Config["Agent:AgentId"];
        var AgentId = string.IsNullOrWhiteSpace(BaseAgentId)
            ? $"exodus-agent-{Nodo}"
            : $"{BaseAgentId}-{Nodo}";

        var Latido = new HeartBeat
        {
            IdAgente = AgentId,
            SistemaOperativo = ObtenerSistemaOperativo(),
            NombreHost = Nodo,
            UltimoLatido = DateTime.UtcNow
        };

        await Elastic.IndexAsync(Latido,
            i => i.Index(Config["Elastic:HeartbeatsIndex"]), pCancelacionToken);
    }

    protected int ObtenerIntervaloRevisionSegundos()
    {
        var Intervalo = Config.GetValue<int>("Agent:IntervaloRevisionEnSegundos");
        return Intervalo > 0 ? Intervalo : 60;
    }

    private (TimeSpan Retencion, int MaximoChecks) ObtenerPoliticaRetencion()
    {
        var Horas = Config.GetValue<int>("Agent:HealthChecksRetencionEnHoras");
        if (Horas <= 0) Horas = 48;

        // Cota superior duro para evitar docs monstruosos: 2×24×60 = 2880 checks
        // a 60 s (48 h). Con margen para drift: 3000.
        var Maximo = Config.GetValue<int>("Agent:HealthChecksMaximo");
        if (Maximo <= 0) Maximo = 3000;

        return (TimeSpan.FromHours(Horas), Maximo);
    }

    /// <summary>
    /// Borra documentos del índice que pertenecen a este agente (mismo NodoHostname
    /// y mismo prefijo de IdServicio) pero cuyos servicios ya no corren localmente.
    /// Esto limpia documentos huérfanos tras migración, scale-down o eliminación
    /// de servicios.
    /// </summary>
    protected async Task LimpiarDocumentosHuerfanosAsync(
        string pNodoHostname,
        string pPrefijoIdServicio,
        HashSet<string> pIdsServiciosActivos,
        CancellationToken pCancelacionToken)
    {
        if (string.IsNullOrWhiteSpace(pNodoHostname)) return;

        try
        {
            var Indice = Config["Elastic:ServicesIndex"];

            var Respuesta = await Elastic.SearchAsync<EstadoServicio>(s => s
                .Index(Indice)
                .Size(1000)
                .Source(src => src.Includes(i => i.Field(f => f.IdServicio)))
                .Query(q => q
                    .Bool(b => b
                        .Must(
                            m => m.Term(t => t.Field("NodoHostname.keyword").Value(pNodoHostname)),
                            m => m.Prefix(p => p.Field("IdServicio.keyword").Value(pPrefijoIdServicio))
                        )
                    )
                ), pCancelacionToken);

            if (!Respuesta.IsValid)
            {
                Logger.LogWarning("Búsqueda de docs huérfanos falló: {Error}",
                    Respuesta.OriginalException?.Message);
                return;
            }

            var IdsEnElastic = Respuesta.Documents.Select(d => d.IdServicio).ToList();
            var IdsAEliminar = IdsEnElastic
                .Where(id => !pIdsServiciosActivos.Contains(id))
                .ToList();

            if (IdsAEliminar.Count == 0) return;

            foreach (var Id in IdsAEliminar)
            {
                await Elastic.DeleteAsync<EstadoServicio>(Id,
                    d => d.Index(Indice), pCancelacionToken);
            }

            Logger.LogInformation(
                "Cleanup: eliminados {Cantidad} docs huérfanos en nodo {Nodo}. IDs: [{Ids}]",
                IdsAEliminar.Count, pNodoHostname, string.Join(", ", IdsAEliminar));
        }
        catch (Exception Ex)
        {
            Logger.LogWarning(Ex, "Error durante cleanup de documentos huérfanos.");
        }
    }
}