namespace Exodus.Agent.Core.Models;

public class ResultadoPruebaUrl
{
    public string Url { get; set; } = "";
    public string Tipo { get; set; } = "";
    public bool Alcanzable { get; set; }
    public int? CodigoHttp { get; set; }
    public long TiempoRespuestaMs { get; set; }
    public string? Detalle { get; set; }
}
