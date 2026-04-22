using Docker.DotNet;
using Exodus.Agent.Core.Services;

namespace Exodus.Agent.Docker.Services;

public class VerificadorDocker : IVerificadorPlataforma
{
    private readonly DockerClient Docker;

    public string NombrePlataforma => "docker-swarm";

    public VerificadorDocker(DockerClient pDocker)
    {
        Docker = pDocker;
    }

    public async Task<bool> EstaAlcanzableAsync(CancellationToken pCancelacionToken)
    {
        try
        {
            await Docker.System.PingAsync(pCancelacionToken);
            return true;
        }
        catch { return false; }
    }
}