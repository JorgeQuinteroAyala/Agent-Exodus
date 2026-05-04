namespace Exodus.Agent.Core.Services;

public class FiltrosComunes
{
    private readonly ProveedorConfiguracionDinamica Configuracion;

    public FiltrosComunes(ProveedorConfiguracionDinamica pConfiguracion)
    {
        Configuracion = pConfiguracion;
    }

    public bool EsServicioIgnorado(string pNombreServicio)
    {
        return Configuracion.Obtener().SetServiciosIgnorados.Contains(pNombreServicio);
    }

    public bool EsDominioBloqueado(string? pUrl)
    {
        if (string.IsNullOrWhiteSpace(pUrl)) return false;

        return Configuracion.Obtener().DominiosBloqueados.Any(d =>
            pUrl.Contains(d, StringComparison.OrdinalIgnoreCase));
    }
}
