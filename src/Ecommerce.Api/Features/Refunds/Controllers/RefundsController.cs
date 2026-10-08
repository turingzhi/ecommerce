using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Refunds.Contracts;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Infrastructure.Persistence;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Ecommerce.Common.Controllers;

namespace Ecommerce.Features.Refunds.Controllers;

[ApiController]
[Route("refunds")]
[Authorize]
public sealed class RefundsController : ControllerBase
{
    [HttpGet("{refundId:guid}")]
    public async Task<IResult> GetById(
        [FromRoute] Guid refundId,
        [FromServices] ShopDbContext db,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var refund = await db.Refunds.AsNoTracking()
            .Where(refund => refund.Id == refundId && db.Payments.Any(payment =>
                payment.Id == refund.PaymentId && db.Orders.Any(order =>
                    order.Id == payment.OrderId && order.CustomerId == customerId)))
            .SingleOrDefaultAsync(cancellationToken);
        return refund is null
            ? Results.NotFound()
            : Results.Ok(RefundResponse.From(refund));
    }

    [HttpGet("/payments/{paymentId:guid}/refunds")]
    public async Task<IResult> List(
        [FromRoute] Guid paymentId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ShopDbContext db,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var owned = await db.Payments.AsNoTracking().AnyAsync(payment =>
            payment.Id == paymentId && db.Orders.Any(order =>
                order.Id == payment.OrderId && order.CustomerId == customerId),
            cancellationToken);
        if (!owned)
            return Results.NotFound();
        var currentPage = Math.Max(page ?? 1, 1);
        var currentPageSize = Math.Clamp(pageSize ?? 10, 1, 50);
        // EF Skip takes an int. Calculate in long so a valid large page
        // cannot overflow into a negative offset or expose the first page.
        var skip = (long)(currentPage - 1) * currentPageSize;
        var refunds = skip > int.MaxValue
            ? new List<RefundResponse>()
            : await db.Refunds.AsNoTracking()
                .Where(refund => refund.PaymentId == paymentId)
                .OrderByDescending(refund => refund.CreatedAt)
                .ThenByDescending(refund => refund.Id)
                .Skip((int)skip)
                .Take(currentPageSize)
                .Select(refund => new RefundResponse(
                    refund.Id, refund.PaymentId, refund.AmountCents,
                    refund.Currency, refund.Status, DateTime.SpecifyKind(refund.CreatedAt, DateTimeKind.Utc)))
                .ToListAsync(cancellationToken);
        return Results.Ok(new RefundListResponse(currentPage, currentPageSize, refunds));
    }

    [HttpPost("/payments/{paymentId:guid}/refunds")]
    [EnableRateLimiting(CommerceRateLimiting.PaymentPolicy)]
    public async Task<IResult> Create(
        [FromRoute] Guid paymentId,
        [FromBody] CreateRefundRequest request,
        [FromServices] ShopDbContext db,
        [FromServices] RefundService service,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.BadRequest(new { error = "Invalid Idempotency Key" });
        if (request.AmountCents <= 0)
            return Results.BadRequest(new { error = "Refund amount must be positive." });

        // RefundService is also used by internal verification; HTTP must verify
        // ownership before calling its payment-ID-based methods.
        var owned = await db.Payments.AsNoTracking().AnyAsync(payment =>
            payment.Id == paymentId && db.Orders.Any(order =>
                order.Id == payment.OrderId && order.CustomerId == customerId),
            cancellationToken);
        if (!owned)
            return Results.NotFound();
        var result = await service.Create(paymentId, request.AmountCents, key);
        if (result.Error is "There is no such payment" or "There is no such order")
            return Results.NotFound();
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });
        return result.Replayed
            ? Results.Ok(RefundResponse.From(result.Refund!))
            : Results.Json(RefundResponse.From(result.Refund!),
                statusCode: StatusCodes.Status201Created);
    }
}
