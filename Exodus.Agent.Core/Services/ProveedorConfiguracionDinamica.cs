using Elasticsearch.Net;
using Exodus.Agent.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Nest;

namespace Exodus.Agent.Core.Services;

public class ProveedorConfiguracionDinamica
{
    private const string IndiceConfig = "exodus-config";
    private const string IdDocumento = "actual";

    private readonly IElasticClient Elastic;
    private readonly IConfiguration Config;
    private readonly ILogger<ProveedorConfiguracionDinamica> Logger;

    private SnapshotConfiguracion _snapshot;

    public ProveedorConfiguracionDinamica(IElasticClient pElastic, IConfiguration pConfig,
        ILogger<ProveedorConfiguracionDinamica> pLogger)
    {
        Elastic = pElastic;
        Config = pConfig;
        Logger = pLogger;
        _snapshot = SnapshotDesdeConfig();
    }

    /// <summary>
    /// Devuelve el snapshot vigente. Lectura sin lock (Volatile.Read sobre referencia
    /// inmutable; atómico en .NET para referencias).
    /// </summary>
    public SnapshotConfiguracion Obtener() => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Consulta el índice <c>exodus-config</c>. Si la versión remota es igual a la
    /// cacheada, no reconstruye nada (1 GET + 1 comparación de long en estado estable).
    /// Si Elastic falla, mantiene el snapshot anterior y loguea warning.
    /// </summary>
    public async Task RefrescarAsync(CancellationToken ct)
    {
        try
        {
            var Respuesta = await Elastic.GetAsync<ConfiguracionAgenteDoc>(IdDocumento,
                g => g.Index(IndiceConfig), ct);

            if (!Respuesta.Found) return;

            var Doc = Respuesta.Source;
            var SnapshotActual = Volatile.Read(ref _snapshot);

            if (Doc.Version == SnapshotActual.Version) return;

            var NuevoSnapshot = SnapshotConfiguracion.Desde(Doc);
            Volatile.Write(ref _snapshot, NuevoSnapshot);
            Logger.LogInformation("Configuración dinámica actualizada a versión {Version}.", Doc.Version);
        }
        catch (Exception Ex)
        {
            Logger.LogWarning(Ex,
                "No se pudo refrescar configuración desde Elasticsearch. " +
                "Se mantiene el snapshot anterior (versión {Version}).",
                Volatile.Read(ref _snapshot).Version);
        }
    }

    /// <summary>
    /// Llamado una vez al arrancar, tras <c>AsegurarIndicesAsync</c>. Si el documento
    /// <c>actual</c> no existe, lo crea con los valores de <c>appsettings.json</c> y
    /// <c>Version=1</c>. Usa <c>OpType.Create</c> para que réplicas concurrentes reciban
    /// un 409 benigno. Tras crear (o si ya existe), refresca el snapshot.
    /// </summary>
    public async Task BootstrapAsync(CancellationToken ct)
    {
        var Doc = new ConfiguracionAgenteDoc
        {
            Version = 1,
            ActualizadoUtc = DateTime.UtcNow,
            ActualizadoPor = "bootstrap",
            ServiciosIgnorados = Config.GetSection("Agent:ServiciosIgnorados")
                .Get<List<string>>() ?? new(),
            // Acepta tanto la clave Docker (ServiciosSoloInterno) como la IIS legacy (SitiosSoloInterno)
            ServiciosSoloInterno = Config.GetSection("Agent:ServiciosSoloInterno")
                .Get<List<string>>()
                ?? Config.GetSection("Agent:SitiosSoloInterno").Get<List<string>>()
                ?? new(),
            DominiosBloqueados = Config.GetSection("Agent:DominiosBloqueados")
                .Get<List<string>>() ?? new()
        };

        try
        {
            var Respuesta = await Elastic.IndexAsync(Doc, i => i
                .Index(IndiceConfig)
                .Id(IdDocumento)
                .OpType(OpType.Create), ct);

            if (Respuesta.IsValid)
                Logger.LogInformation(
                    "Documento de configuración inicial creado en '{Indice}' con versión {Version}.",
                    IndiceConfig, Doc.Version);
            else if (Respuesta.ServerError?.Status == 409)
                Logger.LogDebug(
                    "Documento '{Id}' ya existe en '{Indice}' (409 benigno).",
                    IdDocumento, IndiceConfig);
            else
                Logger.LogWarning(
                    "No se pudo crear el documento bootstrap en '{Indice}': {Error}",
                    IndiceConfig,
                    Respuesta.OriginalException?.Message ?? Respuesta.ServerError?.Error?.Reason);
        }
        catch (Exception Ex)
        {
            Logger.LogWarning(Ex,
                "No se pudo crear el documento bootstrap en '{Indice}'.", IndiceConfig);
        }

        await RefrescarAsync(ct);
    }

    private SnapshotConfiguracion SnapshotDesdeConfig()
    {
        var ServiciosIgnorados = Config.GetSection("Agent:ServiciosIgnorados")
            .Get<List<string>>() ?? new();
        var ServiciosSoloInterno = Config.GetSection("Agent:ServiciosSoloInterno")
            .Get<List<string>>()
            ?? Config.GetSection("Agent:SitiosSoloInterno").Get<List<string>>()
            ?? new();
        var DominiosBloqueados = Config.GetSection("Agent:DominiosBloqueados")
            .Get<List<string>>() ?? new();

        return new SnapshotConfiguracion(0, ServiciosIgnorados, ServiciosSoloInterno, DominiosBloqueados);
    }
}
