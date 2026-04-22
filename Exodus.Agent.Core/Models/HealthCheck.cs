namespace Exodus.Agent.Core.Models;

public class HealthCheck
{
    public DateTime TsUtc { get; set; }
    public string Salud { get; set; } = "";
    public string? UrlExitosa { get; set; }
    public string? Detalle { get; set; }
    public string Estado { get; set; } = "";
    public List<string> HostsRunning { get; set; } = new();
    public int ReplicasRunning { get; set; }
    public int ReplicasDeseadas { get; set; }
    public string SaludInterna { get; set; } = "";
    public string? SaludDetalleInterna { get; set; }
    public string? UrlExitosaInterna { get; set; }
    public string SaludExterna { get; set; } = "";
    public string? SaludDetalleExterna { get; set; }
    public string? UrlExitosaExterna { get; set; }
    public List<ResultadoPruebaUrl> PruebasUrls { get; set; } = new();
    public MetricasRecursos? Recursos { get; set; }
}