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
[Route("admin/shipments")]
[Authorize(Policy = ShipmentAdministration.Policy)]
public sealed class ShipmentAdminController : ControllerBase
{
    [HttpGet("")]
    public async Task<IResult> List(
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ShipmentQueryService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (status is not null && status is not ("Pending" or "Shipped" or "Delivered"))
            return Results.BadRequest(new { error = "Unknown shipment status." });
        return Results.Ok(await service.ListAsync(status, page, pageSize, ct));
    }

    [HttpPut("{shipmentId:guid}/status")]
    public async Task<IResult> UpdateStatus(
        [FromRoute] Guid shipmentId,
        [FromBody] UpdateShipmentStatusRequest request,
        [FromServices] ShipmentService service,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(actor))
            return Results.Unauthorized();
        var result = await service.UpdateStatusAsync(shipmentId, request, actor, cancellationToken);
        return result.Error switch
        {
            ShipmentUpdateError.InvalidRequest => Results.BadRequest(new { error = result.Detail }),
            ShipmentUpdateError.NotFound => Results.NotFound(),
            ShipmentUpdateError.Conflict => Results.Conflict(new { error = result.Detail }),
            _ => Results.Ok(ShipmentResponse.From(result.Shipment!))
        };
    }

    [HttpGet("{shipmentId:guid}/history")]
    public async Task<IResult> GetHistory(
        [FromRoute] Guid shipmentId,
        [FromServices] ShipmentQueryService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await service.GetHistoryAsync(shipmentId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
