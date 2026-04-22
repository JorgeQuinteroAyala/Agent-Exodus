using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;

namespace Exodus.Agent.Core.Services;

public static class DetectorSistemaOperativo
{
    public static string ObtenerSistemaOperativo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "OSX";
        return "Desconocido";
    }

    public static string ObtenerEndpointDocker(IConfiguration pConfig)
    {
        var Configurado = pConfig["Docker:Endpoint"];

        if (!string.IsNullOrWhiteSpace(Configurado))
            return Configurado;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";
    }
}
