using Ecommerce.Features.Shipments.Contracts;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Features.Shipments.Services;
using Ecommerce.Infrastructure.Persistence;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Features.Shipments.Controllers;

[ApiController]
[Route("orders/{orderId:guid}")]
[Authorize]
public sealed class ShipmentsController : ControllerBase
{
    [HttpGet("shipment")]
    public async Task<IResult> GetForOrder(
        [FromRoute] Guid orderId,
        [FromServices] ShopDbContext db,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var shipment = await db.Shipments.AsNoTracking()
            .Where(s => s.OrderId == orderId && db.Orders.Any(o =>
                o.Id == s.OrderId && o.CustomerId == customerId))
            .SingleOrDefaultAsync(cancellationToken);
        return shipment is null ? Results.NotFound() : Results.Ok(ShipmentResponse.From(shipment));
    }

    [HttpGet("tracking")]
    public async Task<IResult> GetTracking(
        [FromRoute] Guid orderId,
        [FromServices] ShipmentQueryService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var result = await service.GetTrackingAsync(customerId, orderId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
