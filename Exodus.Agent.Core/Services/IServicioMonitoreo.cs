namespace Exodus.Agent.Core.Services;

public interface IServicioMonitoreo
{
    string NombrePlataforma { get; }

    Task EjecutarUnaVezAsync(CancellationToken pCancelacionToken);
}
