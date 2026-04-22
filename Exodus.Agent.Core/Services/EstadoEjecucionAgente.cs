namespace Exodus.Agent.Core.Services;

public class EstadoEjecucionAgente
{
    public DateTime InicioUTC { get; } = DateTime.UtcNow;
    public DateTime? UltimaEjecucionExitosaUTC { get; private set; }

    public void MarcarEjecucionExitosa()
    {
        UltimaEjecucionExitosaUTC = DateTime.UtcNow;
    }

    public TimeSpan ObtenerUptime() => DateTime.UtcNow - InicioUTC;
}
