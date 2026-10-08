using Ecommerce.Common.Pagination;
using Ecommerce.Features.Payments.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Features.Payments.Controllers;

[ApiController]
[Route("admin/payments")]
[Authorize(Policy = PaymentAdministration.Policy)]
public sealed class PaymentAdminController : ControllerBase
{
    private static readonly HashSet<string> Statuses = ["Pending", "Succeeded", "Failed", "Unknown"];

    [HttpGet("")]
    public async Task<IResult> List(
        [FromQuery] string? status,
        [FromQuery] Guid? orderId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] PaymentQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (status is not null && !Statuses.Contains(status))
            return Results.BadRequest(new { error = "Status must be Pending, Succeeded, Failed, or Unknown." });
        return Results.Ok(await query.ListForAdminAsync(status, orderId, PageBounds.Normalize(page, pageSize), ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(
        [FromRoute] Guid id,
        [FromServices] PaymentQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var payment = await query.GetForAdminAsync(id, ct);
        return payment is null ? Results.NotFound() : Results.Ok(payment);
    }
}
