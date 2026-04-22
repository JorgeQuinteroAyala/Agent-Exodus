using Exodus.Agent.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Exodus.Agent.Core.Controllers;

[ApiController]
[Route("api/actualizar")]
public class ComandoController : ControllerBase
{
    private readonly IServicioMonitoreo Servicio;

    public ComandoController(IServicioMonitoreo pServicio)
    {
        Servicio = pServicio;
    }

    [HttpPost]
    public async Task<IActionResult> EjecutarActualizacion(CancellationToken pCancelacionToken)
    {
        await Servicio.EjecutarUnaVezAsync(pCancelacionToken);
        return Ok(new { mensaje = "Actualización ejecutada correctamente." });
    }
}
