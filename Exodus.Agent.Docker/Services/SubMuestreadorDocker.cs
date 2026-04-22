using Docker.DotNet;
using Docker.DotNet.Models;
using Exodus.Agent.Core.Services;
using Microsoft.Extensions.Logging;

namespace Exodus.Agent.Docker.Services;

/// <summary>
/// Mapa: containerId → labels del contenedor. Útil para mapear a servicio.
/// </summary>
public record ResultadoSubMuestreoDocker(
    Dictionary<string, List<MuestraRecurso>> MuestrasPorContenedor,
    Dictionary<string, IDictionary<string, string>> LabelsPorContenedor);

public class SubMuestreadorDocker
{
    private readonly DockerClient Docker;
    private readonly ILogger<SubMuestreadorDocker> Logger;
    private readonly SemaphoreSlim Semaforo;

    public SubMuestreadorDocker(DockerClient pDocker,
        ILogger<SubMuestreadorDocker> pLogger,
        int pConcurrenciaMaxima = 8)
    {
        Docker = pDocker;
        Logger = pLogger;
        Semaforo = new SemaphoreSlim(pConcurrenciaMaxima, pConcurrenciaMaxima);
    }

    public async Task<ResultadoSubMuestreoDocker> RecolectarAsync(
    int pNumeroMuestras,
    TimeSpan pIntervaloEntreMuestras,
    CancellationToken pCancelacionToken)
    {
        // FIX: Listar TODOS los contenedores running locales, sin filtro de label.
        // El filtro anterior por "com.docker.swarm.service.id" excluía contenedores
        // que en ese instante no tenían la label propagada (p.ej. justo después de
        // un restart). El cruce con tareas de Swarm lo hace el caller.
        var Contenedores = await Docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = false },
            pCancelacionToken);

        var Muestras = new Dictionary<string, List<MuestraRecurso>>();
        var Labels = new Dictionary<string, IDictionary<string, string>>();

        foreach (var C in Contenedores)
        {
            Muestras[C.ID] = new List<MuestraRecurso>(pNumeroMuestras);
            Labels[C.ID] = C.Labels ?? new Dictionary<string, string>();
        }

        for (int i = 0; i < pNumeroMuestras; i++)
        {
            if (i > 0)
            {
                try { await Task.Delay(pIntervaloEntreMuestras, pCancelacionToken); }
                catch (TaskCanceledException) { break; }
            }

            // Re-snapshot antes de cada muestra para capturar contenedores que
            // aparecieron durante el ciclo (restart, scale-up).
            var ContenedoresActuales = await Docker.Containers.ListContainersAsync(
                new ContainersListParameters { All = false },
                pCancelacionToken);

            foreach (var C in ContenedoresActuales)
            {
                if (!Muestras.ContainsKey(C.ID))
                {
                    // Contenedor nuevo: rellenar con null las muestras anteriores
                    // para mantener el array alineado por índice de tiempo.
                    var Lista = new List<MuestraRecurso>(pNumeroMuestras);
                    for (int k = 0; k < i; k++) Lista.Add(new MuestraRecurso(null, null));
                    Muestras[C.ID] = Lista;
                    Labels[C.ID] = C.Labels ?? new Dictionary<string, string>();
                }
            }

            var Tareas = ContenedoresActuales
                .Select(C => TomarMuestraAsync(C.ID, pCancelacionToken))
                .ToArray();
            var Resultados = await Task.WhenAll(Tareas);

            var IdsPresentes = new HashSet<string>();
            for (int j = 0; j < ContenedoresActuales.Count; j++)
            {
                IdsPresentes.Add(ContenedoresActuales[j].ID);
                Muestras[ContenedoresActuales[j].ID].Add(Resultados[j]);
            }

            // Contenedores que desaparecieron en esta muestra: empujar null
            // para mantener la longitud del array igual en todos.
            foreach (var Id in Muestras.Keys)
            {
                if (!IdsPresentes.Contains(Id) && Muestras[Id].Count < i + 1)
                    Muestras[Id].Add(new MuestraRecurso(null, null));
            }
        }

        return new ResultadoSubMuestreoDocker(Muestras, Labels);
    }

    private async Task<MuestraRecurso> TomarMuestraAsync(string pContainerId,
        CancellationToken pCancelacionToken)
    {
        await Semaforo.WaitAsync(pCancelacionToken);
        try
        {
            ContainerStatsResponse? Respuesta = null;
            var Progreso = new Progress<ContainerStatsResponse>(s => Respuesta = s);

            // Stream=false + OneShot=false → una lectura que incluye precpu_stats.
            await Docker.Containers.GetContainerStatsAsync(
                pContainerId,
                new ContainerStatsParameters { Stream = false, OneShot = false },
                Progreso,
                pCancelacionToken);

            if (Respuesta is null)
                return new MuestraRecurso(null, null);

            return new MuestraRecurso(
                CalcularCpuPorcentaje(Respuesta),
                CalcularMemoriaBytes(Respuesta));
        }
        catch (Exception Ex)
        {
            Logger.LogDebug(Ex, "No se pudo leer stats del contenedor {Id}", pContainerId);
            return new MuestraRecurso(null, null);
        }
        finally { Semaforo.Release(); }
    }

    private static double? CalcularCpuPorcentaje(ContainerStatsResponse pStats)
    {
        if (pStats.CPUStats is null || pStats.PreCPUStats is null) return null;

        var CpuDelta = (double)pStats.CPUStats.CPUUsage.TotalUsage
                     - (double)pStats.PreCPUStats.CPUUsage.TotalUsage;
        var SystemDelta = (double)pStats.CPUStats.SystemUsage
                        - (double)pStats.PreCPUStats.SystemUsage;

        var OnlineCpus = pStats.CPUStats.OnlineCPUs > 0
            ? pStats.CPUStats.OnlineCPUs
            : (uint)(pStats.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);

        if (SystemDelta <= 0 || CpuDelta < 0) return 0;

        return Math.Round(CpuDelta / SystemDelta * OnlineCpus * 100.0, 2);
    }

    private static long? CalcularMemoriaBytes(ContainerStatsResponse pStats)
    {
        if (pStats.MemoryStats is null) return null;

        var Usage = (long)pStats.MemoryStats.Usage;

        // Restar cache (cgroup v1) o inactive_file (cgroup v2) para igualar lo
        // que reporta `docker stats` (RSS real, no incluye page cache).
        if (pStats.MemoryStats.Stats is not null)
        {
            if (pStats.MemoryStats.Stats.TryGetValue("cache", out var Cache))
                Usage -= (long)Cache;
            else if (pStats.MemoryStats.Stats.TryGetValue("inactive_file", out var Inactive))
                Usage -= (long)Inactive;
        }

        return Usage < 0 ? 0 : Usage;
    }
}