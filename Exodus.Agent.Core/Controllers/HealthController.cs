using Exodus.Agent.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Nest;

namespace Exodus.Agent.Core.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IElasticClient Elastic;
    private readonly EstadoEjecucionAgente Estado;
    private readonly IVerificadorPlataforma VerificadorPlataforma;

    public HealthController(IElasticClient pElastic, EstadoEjecucionAgente pEstado,
        IVerificadorPlataforma pVerificadorPlataforma)
    {
        Elastic = pElastic;
        Estado = pEstado;
        VerificadorPlataforma = pVerificadorPlataforma;
    }

    [HttpGet]
    public async Task<IActionResult> GetHealth(CancellationToken pCancelacionToken)
    {
        var ElasticOk = await VerificarElasticAsync(pCancelacionToken);
        var PlataformaOk = await VerificadorPlataforma.EstaAlcanzableAsync(pCancelacionToken);
        var Uptime = Estado.ObtenerUptime();
        var UltimaEjecucion = Estado.UltimaEjecucionExitosaUTC;

        var Status = (ElasticOk && PlataformaOk) ? "ok" : "degraded";

        return StatusCode(Status == "ok" ? 200 : 503, new
        {
            status = Status,
            elastic = ElasticOk ? "connected" : "unreachable",
            plataforma = VerificadorPlataforma.NombrePlataforma,
            plataforma_estado = PlataformaOk ? "connected" : "unreachable",
            uptime_seconds = (int)Uptime.TotalSeconds,
            last_success_utc = UltimaEjecucion?.ToString("O")
        });
    }

    private async Task<bool> VerificarElasticAsync(CancellationToken pCancelacionToken)
    {
        var Ping = await Elastic.PingAsync(p => p, pCancelacionToken);
        return Ping.IsValid;
    }
}
