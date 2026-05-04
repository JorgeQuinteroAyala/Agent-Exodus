using Exodus.Agent.Core.Services;
using Microsoft.Web.Administration;

namespace Exodus.Agent.IIS.Services;

public class VerificadorIIS : IVerificadorPlataforma
{
    public string NombrePlataforma => "iis";

    public Task<bool> EstaAlcanzableAsync(CancellationToken pCancelacionToken)
    {
        try
        {
            using var Administrador = new ServerManager();
            _ = Administrador.Sites.Count;
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
