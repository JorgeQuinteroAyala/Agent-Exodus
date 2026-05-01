using Exodus.Agent.Core.Models;

namespace Exodus.Agent.Core.Services;

/// <summary>
/// Muestra cruda de una sola lectura para una réplica/proceso.
/// </summary>
public record MuestraRecurso(double? CpuPorcentaje, long? MemoriaBytes);

/// <summary>
/// Agrega muestras sub-muestreadas al formato MetricasRecursos.
/// Política "2B" (máximo entre réplicas):
///   1. En cada punto temporal se toma el MÁXIMO entre réplicas (replica peor-caso).
///   2. Sobre la serie resultante de máximos se calcula min/avg/max.
/// </summary>
public static class AgregadorMetricas
{
    /// <param name="pMuestrasPorTiempo">
    /// Índice externo = punto temporal (t0..tN-1).
    /// Índice interno = réplica/proceso en ese punto.
    /// </param>
    public static MetricasRecursos Agregar(
        List<List<MuestraRecurso>> pMuestrasPorTiempo,
        double? pCpuLimiteNucleos,
        long? pMemoriaLimiteBytes,
        string pOrigenMetrica,
        bool pAppPoolCompartido = false)
    {
        var Resultado = new MetricasRecursos
        {
            CpuLimiteNucleos = pCpuLimiteNucleos,
            MemoriaLimiteBytes = pMemoriaLimiteBytes,
            AppPoolCompartido = pAppPoolCompartido,
            OrigenMetrica = pOrigenMetrica
        };

        var MaxCpuPorTiempo = new List<double>();
        var MaxMemPorTiempo = new List<long>();

        foreach (var Muestras in pMuestrasPorTiempo)
        {
            var Cpus = Muestras.Where(m => m.CpuPorcentaje.HasValue)
                               .Select(m => m.CpuPorcentaje!.Value).ToList();
            var Mems = Muestras.Where(m => m.MemoriaBytes.HasValue)
                               .Select(m => m.MemoriaBytes!.Value).ToList();

            if (Cpus.Count > 0) MaxCpuPorTiempo.Add(Cpus.Max());
            if (Mems.Count > 0) MaxMemPorTiempo.Add(Mems.Max());
        }

        if (MaxCpuPorTiempo.Count > 0)
        {
            Resultado.CpuPorcentajeMin = Math.Round(MaxCpuPorTiempo.Min(), 2);
            Resultado.CpuPorcentajeAvg = Math.Round(MaxCpuPorTiempo.Average(), 2);
            Resultado.CpuPorcentajeMax = Math.Round(MaxCpuPorTiempo.Max(), 2);
        }

        if (MaxMemPorTiempo.Count > 0)
        {
            var MemMin = MaxMemPorTiempo.Min();
            var MemMax = MaxMemPorTiempo.Max();
            var MemAvg = (long)MaxMemPorTiempo.Average();

            Resultado.MemoriaBytesMax = MemMax;

            if (pMemoriaLimiteBytes.HasValue && pMemoriaLimiteBytes.Value > 0)
            {
                double Denom = pMemoriaLimiteBytes.Value;
                Resultado.MemoriaPorcentajeMin = Math.Round(MemMin / Denom * 100.0, 2);
                Resultado.MemoriaPorcentajeAvg = Math.Round(MemAvg / Denom * 100.0, 2);
                Resultado.MemoriaPorcentajeMax = Math.Round(MemMax / Denom * 100.0, 2);
            }
        }

        return Resultado;
    }
}