using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Refunds.Contracts;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Returns.Contracts;
using Ecommerce.Features.Returns.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecommerce.Features.Returns.Controllers;

[ApiController]
[Route("orders/{orderId:guid}")]
[Authorize]
public sealed class ReturnsController : ControllerBase
{
    [HttpPost("returns")]
    [EnableRateLimiting(CommerceRateLimiting.OrderPolicy)]
    public async Task<IResult> Create(
        [FromRoute] Guid orderId,
        [FromBody] CreateReturnRequest body,
        [FromServices] ReturnService service,
        [FromServices] ReturnQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(owner))
            return Results.Unauthorized();
        var result = await service.CreateAsync(owner, orderId, HttpContext.Request.Headers["Idempotency-Key"].ToString(), body, ct);
        if (result.Error is not null)
            return Error(result);
        var response = await query.GetForOwnerAsync(owner, orderId, ct);
        return result.Replayed ? Results.Ok(response) : Results.Created($"/orders/{orderId}/return", response);
    }

    [HttpGet("return")]
    public async Task<IResult> GetForOrder(
        [FromRoute] Guid orderId,
        [FromServices] ReturnQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(owner))
            return Results.Unauthorized();
        var result = await query.GetForOwnerAsync(owner, orderId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static IResult Error(ReturnResult result) => result.Error switch
    {
        ReturnError.InvalidRequest => Results.BadRequest(new { error = result.Detail }),
        ReturnError.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = result.Detail })
    };
}
