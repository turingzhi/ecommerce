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
[Route("dev/payments")]
[Authorize]
[DevelopmentOnly]
public sealed class DevelopmentPaymentsController : ControllerBase
{
    [HttpPost("{paymentId:guid}/simulate")]
    [EnableRateLimiting(CommerceRateLimiting.PaymentPolicy)]
    public async Task<IResult> Simulate(
        [FromRoute] Guid paymentId,
        [FromBody] SimulatePaymentRequest request,
        [FromServices] ShopDbContext db,
        [FromServices] PaymentService service,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        if (request.Outcome is not ("success" or "failure" or "timeout"))
            return Results.BadRequest(new { error = "Outcome must be success, failure, or timeout." });

        // The outcome methods are internal simulations taking a payment ID.
        // Check the immutable order ownership before exposing them over HTTP.
        var owned = await db.Payments.AsNoTracking().AnyAsync(payment =>
            payment.Id == paymentId && db.Orders.Any(order =>
                order.Id == payment.OrderId && order.CustomerId == customerId),
            cancellationToken);
        if (!owned)
            return Results.NotFound();

        var result = request.Outcome switch
        {
            "success" => await service.SimulateSuccess(paymentId),
            "failure" => await service.SimulateFailure(paymentId),
            _ => await service.SimulateTimeout(paymentId)
        };
        if (result.Error is "Payment not found" or "Order not found")
            return Results.NotFound();
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });
        return Results.Ok(PaymentResponse.From(result.Payment!));
    }
}
