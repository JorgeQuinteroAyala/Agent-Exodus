namespace Exodus.Agent.Core.Models;

public class ConfiguracionAgenteDoc
{
    public long Version { get; set; }
    public DateTime ActualizadoUtc { get; set; }
    public string ActualizadoPor { get; set; } = "";
    public List<string> ServiciosIgnorados { get; set; } = new();
    public List<string> ServiciosSoloInterno { get; set; } = new();
    public List<string> DominiosBloqueados { get; set; } = new();
}
