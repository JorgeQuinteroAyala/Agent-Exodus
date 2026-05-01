using Exodus.Agent.Core.Models;
using Exodus.Agent.Core.Services;
using Exodus.Agent.IIS.Models;
using Microsoft.Web.Administration;
using Nest;

namespace Exodus.Agent.IIS.Services;

public class ServicioMonitoreoIIS : BaseServicioMonitoreo
{
    private readonly HttpClient HttpClientInterno;
    private readonly HttpClient HttpClientExterno;
    private readonly DetectorSitiosIIS Detector;
    private readonly FiltrosComunes Filtros;
    private readonly SubMuestreadorIIS SubMuestreador;

    public override string NombrePlataforma => "iis";

    private static readonly Func<int, bool> EsCodigoAceptable =
        codigo => codigo >= 200 && codigo < 500;

    public ServicioMonitoreoIIS(IElasticClient pElastic, IConfiguration pConfig,
        IHttpClientFactory pHttpFactory, ILogger<ServicioMonitoreoIIS> pLogger,
        EstadoEjecucionAgente pEstado, DetectorSitiosIIS pDetector,
        FiltrosComunes pFiltros, SubMuestreadorIIS pSubMuestreador)
        : base(pElastic, pConfig, pLogger, pEstado)
    {
        HttpClientInterno = pHttpFactory.CreateClient("ClienteInterno");
        HttpClientExterno = pHttpFactory.CreateClient("ClienteExterno");
        Detector = pDetector;
        Filtros = pFiltros;
        SubMuestreador = pSubMuestreador;
    }

    protected override string ObtenerSistemaOperativo() => "Windows";

    protected override Task<string> ObtenerNombreNodoAsync(CancellationToken pCancelacionToken)
        => Task.FromResult(Environment.MachineName);

    protected override async Task ProcesarCicloAsync(CancellationToken pCancelacionToken)
    {
        var Sitios = Detector.ObtenerSitios()
            .Where(s => !Filtros.EsServicioIgnorado(s.Nombre))
            .ToList();

        var SitiosSoloInterno = new HashSet<string>(
        Config.GetSection("Agent:SitiosSoloInterno").Get<string[]>()
            ?? Array.Empty<string>(),
        StringComparer.OrdinalIgnoreCase);

        // Mapear sitio → app pool usando la root application.
        var SitioAPool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var LimitesPorPool = new Dictionary<string, (double? Cpu, long? Mem)>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            using var Sm = new ServerManager();

            foreach (var Sitio in Sitios)
            {
                var S = Sm.Sites[Sitio.Nombre];
                if (S is null) continue;
                var Root = S.Applications.FirstOrDefault(a => a.Path == "/");
                if (Root is not null)
                    SitioAPool[Sitio.Nombre] = Root.ApplicationPoolName;
            }

            foreach (var PoolName in SitioAPool.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                LimitesPorPool[PoolName] = LeerLimitesAppPool(Sm, PoolName);
        }
        catch (Exception Ex)
        {
            Logger.LogWarning(Ex, "No se pudo leer configuración de App Pools.");
        }

        var PoolsUnicos = SitioAPool.Values
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var ConteoPool = SitioAPool.Values
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Lanzar sub-muestreo en paralelo con URL checks.
        var TareaMuestreo = SubMuestreador.RecolectarAsync(
            4, TimeSpan.FromSeconds(15), PoolsUnicos, pCancelacionToken);

        // Construir docs con URL health (sin recursos todavía).
        var Contextos = new List<ContextoSitio>();
        foreach (var Sitio in Sitios)
        {
            var IdServicio = $"iis:{Sitio.Nombre}";
            var Doc = await ObtenerDocumentoExistenteAsync(IdServicio, pCancelacionToken)
                      ?? new EstadoServicio();
            var Pruebas = await ConstruirEstadoAsync(
                Doc, Sitio, SitiosSoloInterno, pCancelacionToken);
            Contextos.Add(new ContextoSitio(Doc, Sitio, Pruebas));
        }

        var MuestrasPorPool = await TareaMuestreo;

        foreach (var Ctx in Contextos)
        {
            MetricasRecursos? Recursos = null;

            if (SitioAPool.TryGetValue(Ctx.Sitio.Nombre, out var Pool)
                && MuestrasPorPool.TryGetValue(Pool, out var MuestrasPool))
            {
                var Limites = LimitesPorPool.TryGetValue(Pool, out var L)
                    ? L : (Cpu: (double?)null, Mem: (long?)null);
                var Compartido = ConteoPool.TryGetValue(Pool, out var N) && N > 1;

                // En IIS cada punto temporal tiene una sola "réplica" (el pool).
                var PorTiempo = MuestrasPool.Select(m => new List<MuestraRecurso> { m }).ToList();

                Recursos = AgregadorMetricas.Agregar(
                    PorTiempo, Limites.Cpu, Limites.Mem,
                    pOrigenMetrica: "iis-process",
                    pAppPoolCompartido: Compartido);
            }

            AgregarHealthCheckHistorico(Ctx.Doc, DateTime.UtcNow, 1, 1, Ctx.Pruebas, Recursos);
            await IndexarEstadoAsync(Ctx.Doc, pCancelacionToken);
        }

        // Cleanup de sitios IIS eliminados o deshabilitados.
        var IdsActivos = Contextos
            .Select(c => c.Doc.IdServicio)
            .ToHashSet();

        await LimpiarDocumentosHuerfanosAsync(
            pNodoHostname: Environment.MachineName,
            pPrefijoIdServicio: "iis:",
            pIdsServiciosActivos: IdsActivos,
            pCancelacionToken: pCancelacionToken);
    }

    private static (double? Cpu, long? Mem) LeerLimitesAppPool(
        ServerManager pSm, string pPoolName)
    {
        var Pool = pSm.ApplicationPools[pPoolName];
        if (Pool is null) return (null, null);

        double? CpuLim = null;
        // IIS Cpu.Limit se expresa en 1/1000 de %. 100_000 = 100% de 1 core.
        var CpuRaw = Pool.Cpu.Limit;
        if (CpuRaw > 0) CpuLim = CpuRaw / 100_000.0;

        long? MemLim = null;
        // PrivateMemory en Recycling.PeriodicRestart está en KB. 0 = sin límite.
        var MemRaw = Pool.Recycling.PeriodicRestart.PrivateMemory;
        if (MemRaw > 0) MemLim = MemRaw * 1024L;

        return (CpuLim, MemLim);
    }

    private async Task<List<ResultadoPruebaUrl>> ConstruirEstadoAsync(
        EstadoServicio pDocumento, SitioIIS pSitio, HashSet<string> pSitiosSoloInterno, CancellationToken pCancelacionToken)
    {
        var TiempoActual = DateTime.UtcNow;

        var UrlsLocales = pSitio.UrlsLocales
            .Where(u => !Filtros.EsDominioBloqueado(u)).ToList();
        var UrlsInternas = pSitio.UrlsInternas
            .Where(u => !Filtros.EsDominioBloqueado(u)).ToList();
        var UrlsExternas = pSitiosSoloInterno.Contains(pSitio.Nombre)
        ? new List<string>()
        : pSitio.UrlsExternas
            .Where(u => !Filtros.EsDominioBloqueado(u))
            .ToList();

        var UrlsParaCheckInterno = UrlsLocales.Concat(UrlsInternas)
       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        pDocumento.IdServicio = $"iis:{pSitio.Nombre}";
        pDocumento.Nombre = pSitio.Nombre;
        pDocumento.SistemaOperativo = "Windows";
        pDocumento.NombreHost = new List<string> { Environment.MachineName };
        pDocumento.NodoHostname = Environment.MachineName;
        pDocumento.Criticidad = Config[$"Agent:Criticidades:{pSitio.Nombre}"] ?? "Media";
        pDocumento.Replicas = 1;
        pDocumento.UltimaRevision = TiempoActual;
        pDocumento.UltimaActualizacion = TiempoActual;
        pDocumento.TieneHealth = UrlsParaCheckInterno.Count > 0;
        pDocumento.UrlHealth = UrlsLocales.FirstOrDefault() ?? UrlsInternas.FirstOrDefault();
        pDocumento.UrlsProbadas = UrlsParaCheckInterno;

        List<ResultadoPruebaUrl> Pruebas;
        if (pDocumento.TieneHealth)
        {
            var Resultado = await VerificadorHealthHttp.VerificarDualAsync(
                UrlsParaCheckInterno, UrlsExternas,
                HttpClientInterno, HttpClientExterno,
                EsCodigoAceptable, pCancelacionToken);

            pDocumento.Salud = Resultado.SaludGeneral;
            pDocumento.SaludInterna = Resultado.SaludInterna;
            pDocumento.UrlExitosaInterna = Resultado.UrlExitosaInterna;
            pDocumento.SaludDetalleInterna = Resultado.DetalleInterno;
            pDocumento.SaludExterna = Resultado.SaludExterna;
            pDocumento.UrlExitosaExterna = Resultado.UrlExitosaExterna;
            pDocumento.SaludDetalleExterna = Resultado.DetalleExterno;
            pDocumento.UrlExitosa = Resultado.UrlExitosaInterna;
            pDocumento.SaludDetalle = Resultado.DetalleInterno;
            pDocumento.Estado = CalcularEstadoIIS(Resultado.PruebasUrls);
            Pruebas = Resultado.PruebasUrls;
        }
        else
        {
            pDocumento.Salud = "Desconocido";
            pDocumento.UrlExitosa = null;
            pDocumento.SaludDetalle = "Sin URL de health válida.";
            pDocumento.SaludInterna = "";
            pDocumento.SaludDetalleInterna = null;
            pDocumento.UrlExitosaInterna = null;
            pDocumento.SaludExterna = "";
            pDocumento.SaludDetalleExterna = null;
            pDocumento.UrlExitosaExterna = null;
            pDocumento.Estado = "Desconocido";
            Pruebas = new List<ResultadoPruebaUrl>();
        }

        return Pruebas;
    }

    private static string CalcularEstadoIIS(List<ResultadoPruebaUrl> pPruebas)
    {
        if (pPruebas.Count == 0) return "Desconocido";

        var Tiene5xx = pPruebas.Any(p => p.CodigoHttp.HasValue
                                      && p.CodigoHttp.Value >= 500
                                      && p.CodigoHttp.Value < 600);
        if (Tiene5xx) return "Critico";

        var TodasSinCodigo = pPruebas.All(p => !p.CodigoHttp.HasValue);
        if (TodasSinCodigo) return "Critico";

        return "Correcto";
    }

    private record ContextoSitio(
        EstadoServicio Doc,
        SitioIIS Sitio,
        List<ResultadoPruebaUrl> Pruebas);
}