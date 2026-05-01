namespace Exodus.Agent.Core.Models;

public class MetricasRecursos
{
    public double? CpuPorcentajeMin { get; set; }
    public double? CpuPorcentajeAvg { get; set; }
    public double? CpuPorcentajeMax { get; set; }
    public double? CpuLimiteNucleos { get; set; }
    public long? MemoriaBytesMax { get; set; }
    public long? MemoriaLimiteBytes { get; set; }
    public double? MemoriaPorcentajeMin { get; set; }
    public double? MemoriaPorcentajeAvg { get; set; }
    public double? MemoriaPorcentajeMax { get; set; }
    public bool AppPoolCompartido { get; set; }
    public string OrigenMetrica { get; set; } = "sin-datos";
}