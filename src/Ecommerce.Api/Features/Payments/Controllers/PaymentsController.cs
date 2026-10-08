using Ecommerce.Common.Pagination;
using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Payments.Contracts;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Infrastructure.Persistence;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ecommerce.Common.Controllers;

namespace Ecommerce.Features.Payments.Controllers;

[ApiController]
[Route("payments")]
[Authorize]
public sealed class PaymentsController : ControllerBase
{
    [HttpGet("{paymentId:guid}")]
    public async Task<IResult> GetById(
        [FromRoute] Guid paymentId,
        [FromServices] ShopDbContext db,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var payment = await db.Payments.AsNoTracking()
            .Where(payment => payment.Id == paymentId && db.Orders.Any(order =>
                order.Id == payment.OrderId && order.CustomerId == customerId))
            .SingleOrDefaultAsync(cancellationToken);
        return payment is null
            ? Results.NotFound()
            : Results.Ok(PaymentResponse.From(payment));
    }

    [HttpPost("/orders/{orderId:guid}/payments")]
    [EnableRateLimiting(CommerceRateLimiting.PaymentPolicy)]
    public async Task<IResult> Create(
        [FromRoute] Guid orderId,
        [FromServices] PaymentService service)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();

        var key = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.BadRequest(new { error = "Invalid Idempotency Key" });

        var result = await service.Create(customerId, orderId, key);
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });
        return result.Replayed
            ? Results.Ok(PaymentResponse.From(result.Payment!))
            : Results.Json(PaymentResponse.From(result.Payment!), statusCode: StatusCodes.Status201Created);
    }

    [HttpGet("/orders/{orderId:guid}/payments")]
    public async Task<IResult> List(
        [FromRoute] Guid orderId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ShopDbContext db,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(owner))
            return Results.Unauthorized();
        if (!await db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId && o.CustomerId == owner, ct))
            return Results.NotFound();
        var bounds = PageBounds.Normalize(page, pageSize);
        if (bounds.Offset > int.MaxValue)
            return Results.Ok(new PaymentListResponse(bounds.Page, bounds.PageSize, []));
        var rows = await db.Payments
            .AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Skip((int)bounds.Offset)
            .Take(bounds.PageSize)
            .ToListAsync(ct);
        return Results.Ok(new PaymentListResponse(bounds.Page, bounds.PageSize, rows.Select(PaymentResponse.From).ToList()));
    }
}
