using Microsoft.Extensions.Configuration;

namespace Exodus.Agent.Core.Services;

public class FiltrosComunes
{
    private readonly List<string> ServiciosIgnorados;
    private readonly List<string> DominiosBloqueados;

    public FiltrosComunes(IConfiguration pConfig)
    {
        ServiciosIgnorados = pConfig.GetSection("Agent:ServiciosIgnorados")
            .Get<List<string>>() ?? new List<string>();

        DominiosBloqueados = pConfig.GetSection("Agent:DominiosBloqueados")
            .Get<List<string>>() ?? new List<string>();
    }

    public bool EsServicioIgnorado(string pNombreServicio)
    {
        return ServiciosIgnorados.Any(s =>
            s.Equals(pNombreServicio, StringComparison.OrdinalIgnoreCase));
    }

    public bool EsDominioBloqueado(string? pUrl)
    {
        if (string.IsNullOrWhiteSpace(pUrl)) return false;

        return DominiosBloqueados.Any(d =>
            pUrl.Contains(d, StringComparison.OrdinalIgnoreCase));
    }
}
