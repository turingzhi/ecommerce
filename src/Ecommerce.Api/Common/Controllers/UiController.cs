using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ecommerce.Common.Controllers;

namespace Ecommerce.Common.Controllers;

[ApiController]
[Route("ui")]
[AllowAnonymous]
public sealed class UiController : ControllerBase
{
    [HttpGet("config")]
    public IResult GetConfiguration(
        [FromServices] IHostEnvironment environment)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { paymentSimulationEnabled = environment.IsDevelopment() });
    }
}
