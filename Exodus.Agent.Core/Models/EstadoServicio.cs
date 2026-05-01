namespace Exodus.Agent.Core.Models;

public class EstadoServicio
{
    public string IdServicio { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string SistemaOperativo { get; set; } = "";
    public List<string> NombreHost { get; set; } = new();
    public string NodoHostname { get; set; } = "";
    public string Criticidad { get; set; } = "Media";
    public bool TieneHealth { get; set; }
    public string? UrlHealth { get; set; }
    public int Replicas { get; set; }
    public DateTime UltimaRevision { get; set; }
    public DateTime UltimaActualizacion { get; set; }
    public string Estado { get; set; } = "";
    public string Salud { get; set; } = "";
    public List<string> UrlsProbadas { get; set; } = new();
    public string? UrlExitosa { get; set; }
    public string? SaludDetalle { get; set; }
    public string SaludInterna { get; set; } = "";
    public string? SaludDetalleInterna { get; set; }
    public string? UrlExitosaInterna { get; set; }
    public string SaludExterna { get; set; } = "";
    public string? SaludDetalleExterna { get; set; }
    public string? UrlExitosaExterna { get; set; }
    public double? CpuPorcentaje { get; set; }
    public double? MemoriaPorcentaje { get; set; }
    public List<HealthCheck> HealthChecks { get; set; } = new();
}