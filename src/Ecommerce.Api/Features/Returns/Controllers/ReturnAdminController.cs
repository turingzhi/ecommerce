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
[Route("admin/returns")]
[Authorize(Policy = ReturnAdministration.Policy)]
public sealed class ReturnAdminController : ControllerBase
{
    [HttpGet("")]
    public async Task<IResult> List(
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ReturnQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (status is not null && status is not ("Requested" or "Approved" or "Received" or "Completed"))
            return Results.BadRequest(new { error = "Unknown return status." });
        return Results.Ok(await query.ListAsync(status, page, pageSize, ct));
    }

    [HttpPut("{returnId:guid}/status")]
    public async Task<IResult> UpdateStatus(
        [FromRoute] Guid returnId,
        [FromBody] UpdateReturnStatusRequest body,
        [FromServices] ReturnService service,
        [FromServices] ReturnQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await service.UpdateStatusAsync(returnId, body, ct);
        return result.Error is not null ? Error(result) : Results.Ok(await query.GetByIdAsync(returnId, ct));
    }

    [HttpPost("{returnId:guid}/refund")]
    public async Task<IResult> CreateRefund(
        [FromRoute] Guid returnId,
        [FromBody] CreateRefundRequest body,
        [FromServices] ReturnService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var key = HttpContext.Request.Headers["Idempotency-Key"].ToString();
        if (body.AmountCents <= 0 || string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.BadRequest(new { error = "Invalid refund amount or idempotency key." });
        var result = await service.CreateRefundAsync(returnId, body.AmountCents, key, ct);
        if (result.Error == "Return not found")
            return Results.NotFound();
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });
        var response = RefundResponse.From(result.Refund!);
        return result.Replayed ? Results.Ok(response) : Results.Created($"/payments/{response.PaymentId}/refunds", response);
    }

    private static IResult Error(ReturnResult result) => result.Error switch
    {
        ReturnError.InvalidRequest => Results.BadRequest(new { error = result.Detail }),
        ReturnError.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = result.Detail })
    };
}
