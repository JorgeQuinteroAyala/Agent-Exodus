using System.Diagnostics;
using Exodus.Agent.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.Administration;

namespace Exodus.Agent.IIS.Services;

public class SubMuestreadorIIS
{
    private readonly ILogger<SubMuestreadorIIS> Logger;
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime Tiempo)> CacheCpu = new();

    public SubMuestreadorIIS(ILogger<SubMuestreadorIIS> pLogger)
    {
        Logger = pLogger;
    }

    /// <summary>
    /// Retorna mapa AppPool → lista de muestras sumadas sobre todos los w3wp
    /// que pertenecen a ese pool (web gardens incluidos).
    /// </summary>
    public async Task<Dictionary<string, List<MuestraRecurso>>> RecolectarAsync(
        int pNumeroMuestras, TimeSpan pIntervalo,
        IReadOnlyCollection<string> pAppPools,
        CancellationToken pCancelacionToken)
    {
        var Resultado = new Dictionary<string, List<MuestraRecurso>>();
        foreach (var Pool in pAppPools)
            Resultado[Pool] = new List<MuestraRecurso>(pNumeroMuestras);

        for (int i = 0; i < pNumeroMuestras; i++)
        {
            if (i > 0)
            {
                try { await Task.Delay(pIntervalo, pCancelacionToken); }
                catch (TaskCanceledException) { break; }
            }

            var PidsPorPool = EnumerarPidsPorAppPool();

            foreach (var Pool in pAppPools)
            {
                var Muestra = PidsPorPool.TryGetValue(Pool, out var Pids)
                    ? MuestrearPool(Pids)
                    : new MuestraRecurso(null, null);
                Resultado[Pool].Add(Muestra);
            }

            LimpiarCachePidsMuertos(PidsPorPool);
        }

        return Resultado;
    }

    private Dictionary<string, List<int>> EnumerarPidsPorAppPool()
    {
        var Mapa = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var Sm = new ServerManager();
            foreach (var Wp in Sm.WorkerProcesses)
            {
                if (!Mapa.TryGetValue(Wp.AppPoolName, out var Lista))
                {
                    Lista = new List<int>();
                    Mapa[Wp.AppPoolName] = Lista;
                }
                Lista.Add(Wp.ProcessId);
            }
        }
        catch (Exception Ex)
        {
            Logger.LogWarning(Ex, "No se pudieron enumerar los WorkerProcesses de IIS.");
        }
        return Mapa;
    }

    private MuestraRecurso MuestrearPool(List<int> pPids)
    {
        double CpuTotal = 0;
        long MemTotal = 0;
        bool CpuValido = false;
        bool MemValido = false;
        var Ahora = DateTime.UtcNow;

        foreach (var Pid in pPids)
        {
            try
            {
                using var Proc = Process.GetProcessById(Pid);
                var CpuActual = Proc.TotalProcessorTime;
                var Privada = Proc.PrivateMemorySize64;

                if (CacheCpu.TryGetValue(Pid, out var Previa))
                {
                    var CpuDeltaMs = (CpuActual - Previa.Cpu).TotalMilliseconds;
                    var WallDeltaMs = (Ahora - Previa.Tiempo).TotalMilliseconds;

                    if (WallDeltaMs > 0)
                    {
                        var Pct = CpuDeltaMs / WallDeltaMs / Environment.ProcessorCount * 100.0;
                        if (Pct < 0) Pct = 0;
                        CpuTotal += Pct;
                        CpuValido = true;
                    }
                }

                CacheCpu[Pid] = (CpuActual, Ahora);
                MemTotal += Privada;
                MemValido = true;
            }
            catch (ArgumentException) { /* el proceso ya no existe */ }
            catch (InvalidOperationException) { /* ídem */ }
            catch (Exception Ex)
            {
                Logger.LogDebug(Ex, "Error leyendo PID {Pid}", Pid);
            }
        }

        return new MuestraRecurso(
            CpuValido ? Math.Round(CpuTotal, 2) : null,
            MemValido ? MemTotal : null);
    }

    private void LimpiarCachePidsMuertos(Dictionary<string, List<int>> pPidsPorPool)
    {
        var Vivos = new HashSet<int>(pPidsPorPool.Values.SelectMany(l => l));
        foreach (var Pid in CacheCpu.Keys.ToList())
            if (!Vivos.Contains(Pid)) CacheCpu.Remove(Pid);
    }
}