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
[Route("dev/refunds")]
[Authorize]
[DevelopmentOnly]
public sealed class DevelopmentRefundsController : ControllerBase
{
    [HttpPost("{refundId:guid}/simulate")]
    [EnableRateLimiting(CommerceRateLimiting.PaymentPolicy)]
    public async Task<IResult> Simulate(
        [FromRoute] Guid refundId,
        [FromBody] SimulateRefundRequest request,
        [FromServices] ShopDbContext db,
        [FromServices] RefundService service,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        if (request.Outcome is not ("success" or "failure" or "timeout"))
            return Results.BadRequest(new { error = "Outcome must be success, failure, or timeout." });
        var owned = await db.Refunds.AsNoTracking().AnyAsync(refund =>
            refund.Id == refundId && db.Payments.Any(payment =>
                payment.Id == refund.PaymentId && db.Orders.Any(order =>
                    order.Id == payment.OrderId && order.CustomerId == customerId)),
            cancellationToken);
        if (!owned)
            return Results.NotFound();
        var result = request.Outcome switch
        {
            "success" => await service.SimulateSuccess(refundId),
            "failure" => await service.SimulateFailure(refundId),
            _ => await service.SimulateTimeout(refundId)
        };
        if (result.Error is "Refund not found" or "There is no payment")
            return Results.NotFound();
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });
        return Results.Ok(RefundResponse.From(result.Refund!));
    }
}
