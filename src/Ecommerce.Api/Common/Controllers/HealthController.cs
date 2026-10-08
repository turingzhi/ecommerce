using Ecommerce.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Common.Controllers;

[ApiController]
[Route("health")]
[AllowAnonymous]
public sealed class HealthController : ControllerBase
{
    [HttpGet("")]
    public async Task<IResult> Check(
        [FromServices] ShopDbContext db)
    {
        try
        {
            return await db.Database.CanConnectAsync()
                ? Results.Ok(new { status = "healthy" })
                : Results.Json(new { status = "unhealthy" }, statusCode: 503);
        }
        catch (Exception)
        {
            return Results.Json(new { status = "unhealthy" }, statusCode: 503);
        }
    }

    [HttpGet("live")]
    public IResult Liveness()
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { status = "healthy" });
    }

    [HttpGet("dependencies")]
    public async Task<IResult> Dependencies(
        [FromServices] HealthCheckService healthChecks,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var report = await healthChecks.CheckHealthAsync(cancellationToken);
        var healthy = report.Status == HealthStatus.Healthy;
        return Results.Json(new
        {
            status = healthy ? "healthy" : "unhealthy",
            dependencies = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status == HealthStatus.Healthy ? "healthy" : "unhealthy")
        }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
