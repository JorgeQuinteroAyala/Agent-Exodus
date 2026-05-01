namespace Exodus.Agent.Core.Models;

public class HeartBeat
{
    public string TipoDocumento { get; set; } = "HeartBeat";
    public string IdAgente { get; set; } = "";
    public string Estado { get; set; } = "Ok";
    public string NombreHost { get; set; } = "";
    public string SistemaOperativo { get; set; } = "";
    public DateTime UltimoLatido { get; set; }
}
