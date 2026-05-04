namespace Exodus.Agent.Core.Services;

public interface IVerificadorPlataforma
{
    string NombrePlataforma { get; }

    Task<bool> EstaAlcanzableAsync(CancellationToken pCancelacionToken);
}
