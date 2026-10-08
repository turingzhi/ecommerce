using Ecommerce.Common.Pagination;
using Ecommerce.Features.Orders.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Features.Orders.Controllers;

[ApiController]
[Route("admin/orders")]
[Authorize(Policy = OrderAdministration.Policy)]
public sealed class OrderAdminController : ControllerBase
{
    private static readonly HashSet<string> Statuses = ["PendingPayment", "Paid", "Cancelled"];

    [HttpGet("")]
    public async Task<IResult> List(
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] OrderQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (status is not null && !Statuses.Contains(status))
            return Results.BadRequest(new { error = "Status must be PendingPayment, Paid, or Cancelled." });
        return Results.Ok(await query.ListForAdminAsync(status, PageBounds.Normalize(page, pageSize), ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(
        [FromRoute] Guid id,
        [FromServices] OrderQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var order = await query.GetForAdminAsync(id, ct);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}
