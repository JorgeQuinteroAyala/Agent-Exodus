using Docker.DotNet;
using Docker.DotNet.Models;
using Exodus.Agent.Core.Models;
using Exodus.Agent.Core.Services;
using Nest;

using DockerTaskState = Docker.DotNet.Models.TaskState;

namespace Exodus.Agent.Docker.Services;

public class ServicioMonitoreoSwarm : BaseServicioMonitoreo
{
    private readonly DockerClient Docker;
    private readonly HttpClient HttpClientInterno;
    private readonly HttpClient HttpClientExterno;
    private readonly FiltrosComunes Filtros;
    private readonly SubMuestreadorDocker SubMuestreador;
    private readonly string SistemaOperativoCache;

    private string NodoHostnameCache = "";
    private string NodoIdCache = "";

    public override string NombrePlataforma => "docker-swarm";

    private record UrlDescubierta(string Url, bool EsquemaHttpDeclarado, bool EsquemaHttpsDeclarado);

    // Docker: sólo 2xx cuenta como alcanzable.
    private static readonly Func<int, bool> EsCodigoAceptable =
        codigo => codigo >= 200 && codigo < 300;

    public ServicioMonitoreoSwarm(IElasticClient pElastic, IConfiguration pConfig,
        IHttpClientFactory pHttpFactory, ILogger<ServicioMonitoreoSwarm> pLogger,
        EstadoEjecucionAgente pEstado, FiltrosComunes pFiltros,
        DockerClient pDocker, SubMuestreadorDocker pSubMuestreador)
        : base(pElastic, pConfig, pLogger, pEstado)
    {
        HttpClientInterno = pHttpFactory.CreateClient("MiClienteApi");
        HttpClientExterno = pHttpFactory.CreateClient("ClienteExterno");
        Filtros = pFiltros;
        Docker = pDocker;
        SubMuestreador = pSubMuestreador;
        SistemaOperativoCache = DetectorSistemaOperativo.ObtenerSistemaOperativo();
    }

    protected override string ObtenerSistemaOperativo() => SistemaOperativoCache;

    protected override async Task ProcesarCicloAsync(CancellationToken pCancelacionToken)
    {
        await AsegurarIdentidadNodoAsync(pCancelacionToken);

        // Arrancar sub-muestreo en paralelo con el trabajo del ciclo.
        var TareaMuestreo = SubMuestreador.RecolectarAsync(
            pNumeroMuestras: 4,
            pIntervaloEntreMuestras: TimeSpan.FromSeconds(15),
            pCancelacionToken);

        var Servicios = await Docker.Swarm.ListServicesAsync(
            new ServicesListParameters(), pCancelacionToken);

        // Servicios cuyo canal externo se debe omitir aunque tengan URLs HTTPS válidas.
        // El canal interno se ejecuta normalmente, incluso para esas URLs HTTPS.
        var ServiciosSoloInterno = new HashSet<string>(
            Config.GetSection("Agent:ServiciosSoloInterno").Get<string[]>()
                ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var Contextos = new List<ContextoServicio>();

        foreach (var Servicio in Servicios)
        {
            var NombreServicio = Servicio.Spec?.Name ?? "";
            if (Filtros.EsServicioIgnorado(NombreServicio))
                continue;

            var TareasLocales = await ListarTareasLocalesAsync(Servicio.ID, pCancelacionToken);
            if (TareasLocales.Count == 0)
                continue;

            var Contexto = await ConstruirEstadoSinRecursosAsync(
                Servicio, TareasLocales, ServiciosSoloInterno, pCancelacionToken);
            Contextos.Add(Contexto);
        }

        var ResultadoMuestreo = await TareaMuestreo;

        // FIX: refrescar containerIds locales DESPUÉS del sub-muestreo para capturar
        // contenedores reiniciados durante los 60 s del ciclo.
        var IdsContenedoresLocales = await ObtenerIdsContenedoresPorServicioAsync(
            Servicios.Select(s => s.ID).ToList(), pCancelacionToken);

        foreach (var Contexto in Contextos)
        {
            var Recursos = ConstruirMetricasRecursos(
                Contexto.Servicio, IdsContenedoresLocales, ResultadoMuestreo);

            AgregarHealthCheckHistorico(Contexto.Doc, Contexto.TiempoActual,
                Contexto.ReplicasRunning, Contexto.ReplicasDeseadas,
                Contexto.PruebasUrls, Recursos);

            await IndexarEstadoAsync(Contexto.Doc, pCancelacionToken);
        }

        // Cleanup de documentos huérfanos: servicios que ya no corren en este nodo
        // (migrados, scale-down o eliminados) deben removerse del índice.
        var IdsActivos = Contextos
            .Select(c => c.Doc.IdServicio)
            .ToHashSet();

        await LimpiarDocumentosHuerfanosAsync(
            pNodoHostname: NodoHostnameCache,
            pPrefijoIdServicio: "swarm:",
            pIdsServiciosActivos: IdsActivos,
            pCancelacionToken: pCancelacionToken);
    }

    private async Task<Dictionary<string, List<string>>> ObtenerIdsContenedoresPorServicioAsync(
    List<string> pServiciosId, CancellationToken pCancelacionToken)
    {
        var Resultado = new Dictionary<string, List<string>>();
        try
        {
            // Listar contenedores locales con la label de servicio. Un solo roundtrip.
            var Contenedores = await Docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = false,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            ["com.docker.swarm.service.id"] = true
                        }
                    }
                }, pCancelacionToken);

            foreach (var C in Contenedores)
            {
                if (C.Labels is null) continue;
                if (!C.Labels.TryGetValue("com.docker.swarm.service.id", out var SvcId))
                    continue;

                if (!Resultado.TryGetValue(SvcId, out var Lista))
                {
                    Lista = new List<string>();
                    Resultado[SvcId] = Lista;
                }
                Lista.Add(C.ID);
            }
        }
        catch (Exception Ex)
        {
            Logger.LogWarning(Ex, "No se pudieron listar contenedores locales para mapeo de recursos.");
        }
        return Resultado;
    }

    private async Task AsegurarIdentidadNodoAsync(CancellationToken pCancelacionToken)
    {
        if (!string.IsNullOrEmpty(NodoIdCache) && !string.IsNullOrEmpty(NodoHostnameCache))
            return;

        try
        {
            var Info = await Docker.System.GetSystemInfoAsync(pCancelacionToken);
            if (!string.IsNullOrWhiteSpace(Info?.Swarm?.NodeID))
            {
                NodoIdCache = Info.Swarm.NodeID;
                try
                {
                    var Nodo = await Docker.Swarm.InspectNodeAsync(
                        Info.Swarm.NodeID, pCancelacionToken);
                    NodoHostnameCache = Nodo?.Description?.Hostname ?? Environment.MachineName;
                }
                catch { NodoHostnameCache = Environment.MachineName; }
            }
        }
        catch { /* fallback abajo */ }

        if (string.IsNullOrEmpty(NodoHostnameCache))
            NodoHostnameCache = Environment.GetEnvironmentVariable("HOSTNAME")
                             ?? Environment.MachineName;
    }

    private async Task<List<TaskResponse>> ListarTareasLocalesAsync(string pServicioId,
        CancellationToken pCancelacionToken)
    {
        var Filtros = new Dictionary<string, IDictionary<string, bool>>
        {
            ["service"] = new Dictionary<string, bool> { [pServicioId] = true },
            ["desired-state"] = new Dictionary<string, bool> { ["running"] = true }
        };

        if (!string.IsNullOrEmpty(NodoIdCache))
            Filtros["node"] = new Dictionary<string, bool> { [NodoIdCache] = true };

        var Tareas = await Docker.Tasks.ListAsync(
            new TasksListParameters { Filters = Filtros }, pCancelacionToken);

        return Tareas
            .Where(t => t.Status?.State == DockerTaskState.Running)
            .ToList();
    }

    private async Task<ContextoServicio> ConstruirEstadoSinRecursosAsync(
        SwarmService pServicio, List<TaskResponse> pTareasLocales,
        HashSet<string> pServiciosSoloInterno,
        CancellationToken pCancelacionToken)
    {
        var TiempoActual = DateTime.UtcNow;
        var Etiquetas = pServicio.Spec?.Labels ?? new Dictionary<string, string>();
        var NombreServicio = pServicio.Spec?.Name ?? "";

        var SoServicio = Etiquetas.TryGetValue("sistema_operativo", out var SoLabel)
            ? SoLabel : SistemaOperativoCache;

        var IdDocumento = $"swarm:{NombreServicio}:{NodoHostnameCache}";

        var ReplicasDeseadas = (int)(pServicio.Spec?.Mode?.Replicated?.Replicas ?? 0);
        var ReplicasRunningLocal = pTareasLocales.Count;

        var Doc = await ObtenerDocumentoExistenteAsync(IdDocumento, pCancelacionToken)
                  ?? new EstadoServicio();

        Doc.IdServicio = IdDocumento;
        Doc.Nombre = NombreServicio;
        Doc.SistemaOperativo = SoServicio;
        Doc.NombreHost = new List<string> { NodoHostnameCache };
        Doc.NodoHostname = NodoHostnameCache;
        Doc.Criticidad = Etiquetas.TryGetValue("criticidad", out var Crit) ? Crit : "Media";
        Doc.Replicas = ReplicasDeseadas;
        Doc.UltimaRevision = TiempoActual;
        Doc.UltimaActualizacion = TiempoActual;
        Doc.Estado = CalcularEstadoLocal(pTareasLocales);

        var UrlsEtiqueta = Etiquetas.TryGetValue("url_health", out var UrlEtiqueta)
     ? SepararUrls(UrlEtiqueta)
         .Select(u => new UrlDescubierta(u, false, false))  // sin esquema declarado
         .ToList()
     : new List<UrlDescubierta>();

        var UrlsTraefik = ObtenerUrlsDesdeTraefik(Etiquetas, NombreServicio).ToList();

        var UrlsCrudas = UrlsEtiqueta.Concat(UrlsTraefik)
            .Where(u => !string.IsNullOrWhiteSpace(u.Url))
            .ToList();

        var UrlsValidas = UrlsCrudas
            .Where(u => !Filtros.EsDominioBloqueado(u.Url))
            .ToList();

        var UrlsParaProbar = ConstruirVariantesHealth(UrlsValidas);

        Doc.TieneHealth = UrlsParaProbar.Count > 0;
        Doc.UrlHealth = UrlsValidas.Select(u => u.Url).FirstOrDefault();
        Doc.UrlsProbadas = UrlsParaProbar;

        List<ResultadoPruebaUrl> Pruebas;

        if (Doc.TieneHealth)
        {
            // Si el servicio está en ServiciosSoloInterno, omitimos el canal externo
            // aunque haya URLs HTTPS. El interno se ejecuta con UrlsParaProbar intacto.
            var UrlsHttps = pServiciosSoloInterno.Contains(NombreServicio)
                ? new List<string>()
                : UrlsParaProbar
                    .Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var Resultado = await VerificadorHealthHttp.VerificarDualAsync(
                UrlsParaProbar, UrlsHttps,
                HttpClientInterno, HttpClientExterno,
                EsCodigoAceptable, pCancelacionToken);

            Doc.SaludInterna = Resultado.SaludInterna;
            Doc.SaludDetalleInterna = Resultado.DetalleInterno;
            Doc.UrlExitosaInterna = Resultado.UrlExitosaInterna;
            Doc.SaludExterna = Resultado.SaludExterna;
            Doc.SaludDetalleExterna = Resultado.DetalleExterno;
            Doc.UrlExitosaExterna = Resultado.UrlExitosaExterna;
            Doc.Salud = Resultado.SaludGeneral;
            Doc.UrlExitosa = Resultado.UrlExitosaInterna;
            Doc.SaludDetalle = Resultado.DetalleInterno;
            Pruebas = Resultado.PruebasUrls;
        }
        else
        {
            Doc.Salud = "Desconocido";
            Doc.UrlExitosa = null;
            Doc.SaludDetalle = "Sin URL de health válida.";
            Doc.SaludInterna = "";
            Doc.SaludDetalleInterna = null;
            Doc.UrlExitosaInterna = null;
            Doc.SaludExterna = "";
            Doc.SaludDetalleExterna = null;
            Doc.UrlExitosaExterna = null;
            Pruebas = new List<ResultadoPruebaUrl>();
        }

        return new ContextoServicio(Doc, pServicio, pTareasLocales,
            TiempoActual, ReplicasRunningLocal, ReplicasDeseadas, Pruebas);
    }

    private MetricasRecursos ConstruirMetricasRecursos(
    SwarmService pServicio,
    Dictionary<string, List<string>> pIdsPorServicio,
    ResultadoSubMuestreoDocker pMuestreo)
    {
        var Limits = pServicio.Spec?.TaskTemplate?.Resources?.Limits;
        double? CpuLimite = null;
        long? MemLimite = null;

        if (Limits is not null)
        {
            if (Limits.NanoCPUs > 0)
                CpuLimite = Limits.NanoCPUs / 1_000_000_000.0;
            if (Limits.MemoryBytes > 0)
                MemLimite = Limits.MemoryBytes;
        }

        var IdsContenedor = pIdsPorServicio.TryGetValue(pServicio.ID, out var Lista)
            ? Lista : new List<string>();

        int NumeroMuestras = pMuestreo.MuestrasPorContenedor.Values
            .Select(l => l.Count).DefaultIfEmpty(0).Max();

        var PorTiempo = new List<List<MuestraRecurso>>();
        int ConteoRellenos = 0;

        for (int t = 0; t < NumeroMuestras; t++)
        {
            var MuestrasEseTiempo = new List<MuestraRecurso>();
            foreach (var Id in IdsContenedor)
            {
                if (pMuestreo.MuestrasPorContenedor.TryGetValue(Id, out var ListaMuestras)
                    && t < ListaMuestras.Count)
                {
                    MuestrasEseTiempo.Add(ListaMuestras[t]);
                }
                else
                {
                    ConteoRellenos++;
                }
            }
            PorTiempo.Add(MuestrasEseTiempo);
        }

        // Log de diagnóstico: detectar mismatch entre tareas y muestras.
        if (IdsContenedor.Count > 0 && NumeroMuestras > 0)
        {
            var MuestrasValidas = PorTiempo.SelectMany(l => l)
                .Count(m => m.CpuPorcentaje.HasValue || m.MemoriaBytes.HasValue);
            if (MuestrasValidas == 0)
            {
                Logger.LogWarning(
                    "Servicio {Servicio} tiene {Tareas} contenedor(es) local(es) pero 0 muestras válidas. " +
                    "IDs: [{Ids}]",
                    pServicio.Spec?.Name, IdsContenedor.Count, string.Join(", ", IdsContenedor));
            }
        }

        return AgregadorMetricas.Agregar(
            PorTiempo, CpuLimite, MemLimite,
            pOrigenMetrica: "docker-stats",
            pAppPoolCompartido: false);
    }

    private static string CalcularEstadoLocal(List<TaskResponse> pTareasLocales)
    {
        if (pTareasLocales.Count == 0) return "Desconocido";
        // Si todas las tareas locales están Running, localmente está Correcto.
        // El caller garantiza que sólo llamamos aquí si hay tareas running locales.
        return "Correcto";
    }

    protected override async Task<string> ObtenerNombreNodoAsync(
        CancellationToken pCancelacionToken)
    {
        await AsegurarIdentidadNodoAsync(pCancelacionToken);
        return !string.IsNullOrEmpty(NodoHostnameCache)
            ? NodoHostnameCache
            : (Environment.GetEnvironmentVariable("HOSTNAME") ?? "Desconocido");
    }

    // ===== Helpers de URLs (sin cambios respecto a la versión anterior) =====

    private static List<string> SepararUrls(string? pCrudo)
    {
        if (string.IsNullOrWhiteSpace(pCrudo)) return new List<string>();
        return pCrudo.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries
                                                   | StringSplitOptions.TrimEntries)
                     .ToList();
    }

    private static IEnumerable<UrlDescubierta> ObtenerUrlsDesdeTraefik(
    IDictionary<string, string> pEtiquetas, string? pNombreServicio)
    {
        foreach (var Kvp in pEtiquetas)
        {
            if (!Kvp.Key.StartsWith("traefik.http.routers.")) continue;
            if (!Kvp.Key.EndsWith(".rule")) continue;

            var Prefijo = Kvp.Key[..^".rule".Length];

            bool HttpsDeclarado = pEtiquetas.TryGetValue($"{Prefijo}.tls", out var Tls)
                                  && string.Equals(Tls, "true", StringComparison.OrdinalIgnoreCase);

            bool HttpDeclarado = false;

            if (pEtiquetas.TryGetValue($"{Prefijo}.entrypoints", out var Ep))
            {
                // entrypoints puede ser CSV: "http", "https", "http,https", "websecure", etc.
                var EpLower = Ep.ToLowerInvariant();
                if (EpLower.Contains("https") || EpLower.Contains("websecure"))
                    HttpsDeclarado = true;
                if (EpLower.Split(',', ' ').Any(p => p.Trim() is "http" or "web"))
                    HttpDeclarado = true;
            }

            // Si no hay info de entrypoints ni tls, asumir http plano (default de Traefik).
            if (!HttpDeclarado && !HttpsDeclarado)
                HttpDeclarado = true;

            var Regla = Kvp.Value ?? "";
            var IdxHost = Regla.IndexOf("Host(", StringComparison.OrdinalIgnoreCase);
            while (IdxHost >= 0)
            {
                var Cierre = Regla.IndexOf(')', IdxHost);
                if (Cierre < 0) break;

                var Contenido = Regla.Substring(IdxHost + 5, Cierre - IdxHost - 5);
                foreach (var Item in Contenido.Split(','))
                {
                    var Limpio = Item.Trim().Trim('`', '"', '\'');
                    if (string.IsNullOrEmpty(Limpio)) continue;

                    if (HttpDeclarado)
                        yield return new UrlDescubierta($"http://{Limpio}", true, false);
                    if (HttpsDeclarado)
                        yield return new UrlDescubierta($"https://{Limpio}", false, true);
                }

                IdxHost = Regla.IndexOf("Host(", Cierre, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Construye variantes raíz + /health. Sólo cruza esquemas si la URL no tiene
    /// un esquema declarado por Traefik. Si Traefik dice "sólo http", no generamos
    /// variantes https (y viceversa) para evitar 404 del default backend.
    /// </summary>
    private static List<string> ConstruirVariantesHealth(List<UrlDescubierta> pUrls)
    {
        var Resultado = new List<string>();

        foreach (var Desc in pUrls)
        {
            var Base = Desc.Url.TrimEnd('/');
            AgregarSiNoExiste(Resultado, Base);
            AgregarSiNoExiste(Resultado, Base + "/health");

            // Si la URL viene "cruda" (sin esquema declarado), probar también el opuesto.
            // Si viene de Traefik con esquema declarado, NO cruzar.
            if (!Desc.EsquemaHttpDeclarado && !Desc.EsquemaHttpsDeclarado)
            {
                var Opuesto = Base.StartsWith("https://")
                    ? "http://" + Base[8..]
                    : "https://" + Base[7..];
                AgregarSiNoExiste(Resultado, Opuesto);
                AgregarSiNoExiste(Resultado, Opuesto + "/health");
            }
        }

        return Resultado;
    }

    private static void AgregarSiNoExiste(List<string> pLista, string pUrl)
    {
        if (!pLista.Contains(pUrl, StringComparer.OrdinalIgnoreCase))
            pLista.Add(pUrl);
    }

    private record ContextoServicio(
        EstadoServicio Doc,
        SwarmService Servicio,
        List<TaskResponse> TareasLocales,
        DateTime TiempoActual,
        int ReplicasRunning,
        int ReplicasDeseadas,
        List<ResultadoPruebaUrl> PruebasUrls);
}