using Exodus.Agent.Core.Models;

namespace Exodus.Agent.Core.Services;

public sealed record SnapshotConfiguracion(
    long Version,
    IReadOnlyList<string> ServiciosIgnorados,
    IReadOnlyList<string> ServiciosSoloInterno,
    IReadOnlyList<string> DominiosBloqueados)
{
    public HashSet<string> SetServiciosIgnorados { get; } =
        new(ServiciosIgnorados, StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SetServiciosSoloInterno { get; } =
        new(ServiciosSoloInterno, StringComparer.OrdinalIgnoreCase);

    public static SnapshotConfiguracion Desde(ConfiguracionAgenteDoc d) =>
        new(d.Version, d.ServiciosIgnorados, d.ServiciosSoloInterno, d.DominiosBloqueados);
}
