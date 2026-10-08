using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Observability;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Ecommerce.Features.Refunds.Services;

public record RefundResult(
    Refund? Refund,
    bool Replayed = false,
    string? Error = null
);

public class RefundService(ShopDbContext db)
{
    private async Task<RefundResult> CreateMeasuredCore(
        Guid paymentId,
        long amountCents,
        string idempotencyKey)
    {
        if (amountCents <= 0)
        {
            return new(null, false, "Refund amount must be positive");
        }
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new(null, false, "Idempotency key is required");
        }

        if (idempotencyKey.Length > 100)
        {
            return new(null, false, "Idempotency key must not exceed 100 characters");
        }
        await using var transaction =
            await db.Database.BeginTransactionAsync();


        // 1. Load the payment that this refund is intended to reverse.
        // The payment provides the original charge amount and currency.
        var payments = db.Database.IsSqlServer()
            ? db.Payments.FromSqlInterpolated(
                $"SELECT * FROM dbo.Payments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {paymentId}")
            : db.Payments.Where(p => p.Id == paymentId);

        var payment = await payments
            .AsNoTracking()
            .SingleOrDefaultAsync();


        // 2. Reject the request if the payment does not exist.
        if (payment is null)
        {
            return new(null, false, "There is no such payment");
        }


        // 3. Load the order associated with the payment.
        var order = await db.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(
                o => o.Id == payment.OrderId
            );

        if (order is null)
        {
            return new(null, false, "There is no such order");
        }


        // 4. Verify that this payment is eligible for our current refund flow.
        //    For now, we are designing refunds for a successful payment
        //    belonging to an order that is Paid.
        if (payment.Status != "Succeeded")
        {
            return new(null, false, "Only a successful payment can be refunded");
        }


        if (order.Status != "Paid")
        {
            return new(null, false, "The order is not eligible for refund");
        }



        // 5. Look for an existing refund for this payment and idempotency key.
        //    If it exists, return the same refund as a replay instead of
        //    creating another refund.
        var existing = await db.Refunds
            .SingleOrDefaultAsync(
                refund => refund.PaymentId == paymentId &&
                refund.IdempotencyKey == idempotencyKey
            );

        if (existing is not null)
        {
            if (existing.AmountCents != amountCents)
            {
                return new(
                    null,
                    false,
                    "Idempotency key was already used with a different refund amount"
                );
            }

            return new(existing, true);
        }


        // 6. Check whether another unresolved refund already exists for
        //    this payment. A different key must not create another refund
        //    while an existing refund is Pending or Unknown.
        var result = await db.Refunds.AnyAsync(
            r => r.PaymentId == paymentId &&
            (r.Status == "Pending" || r.Status == "Unknown")
        );
        if (result)
        {
            return new(null, false, "An unresolved refund already exists for this payment");
        }


        // 7. Calculate the remaining refundable amount.
        // Use the original payment's currency and never allow the total
        // reserved/refunded amount to exceed the original charge.

        var existingRefunds = await db.Refunds
            .Where(r => r.PaymentId == paymentId &&
              (r.Status == "Succeeded" ||
                r.Status == "Pending" ||
                r.Status == "Unknown"))
            .ToListAsync();

        long reservedRefundAmount = 0;
        foreach (var existingRefund in existingRefunds)
        {
            reservedRefundAmount += existingRefund.AmountCents;
        }

        var remainingRefundable =
        payment.AmountCents - reservedRefundAmount;

        if (amountCents > remainingRefundable)
        {
            return new(null, false, "Refund amount exceeds the remaining refundable amount");
        }

     

        var refund = new Refund
        {
            PaymentId = paymentId,
            AmountCents = amountCents,
            Currency = payment.Currency,
            IdempotencyKey = idempotencyKey
        };


        // 8. Add the refund to the DbContext.
        db.Refunds.Add(refund);


        // 9. Save the refund atomically and commit the transaction.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        //    No external payment-provider call belongs inside this transaction.
        return new(refund);

    }
    private async Task<RefundResult> SimulateSuccessMeasuredCore(Guid refundId)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var (refund, payment) = await LoadRefundAndPaymentForOutcome(refundId);

        if (refund is null)
        {
            return new(null, false, "Refund not found");
        }

        if (refund.Status == "Succeeded")
        {
            return new(refund, true);
        }

        if (refund.Status != "Pending" && refund.Status != "Unknown")
        {
            return new(null, false, "Refund cannot succeed");
        }

        if (payment is null)
        {
            return new(null, false, "There is no payment");
        }

        refund.Status = "Succeeded";

        db.Outbox.Add(new OutboxMessage
        {
            OrderId = payment.OrderId,
            Type = "RefundSucceeded",
            Payload = JsonSerializer.Serialize(new
            {
                OrderId = payment.OrderId,
                PaymentId = payment.Id,
                RefundId = refund.Id,
                AmountCents = refund.AmountCents,
                Currency = refund.Currency
            })
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new(refund);

    }
    private async Task<RefundResult> SimulateFailureMeasuredCore(Guid refundId)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var (refund, payment) = await LoadRefundAndPaymentForOutcome(refundId);

        if (refund is null)
        {
            return new(null, false, "Refund not found");
        }

        if (refund.Status == "Failed")
        {
            return new(refund, true);
        }

        if (refund.Status != "Pending" && refund.Status != "Unknown")
        {
            return new(null, false, "Refund cannot be marked failed");
        }

        if (payment is null)
        {
            return new(null, false, "There is no payment");
        }

        refund.Status = "Failed";

        db.Outbox.Add(new OutboxMessage
        {
            OrderId = payment.OrderId,
            Type = "RefundFailed",
            Payload = JsonSerializer.Serialize(new
            {
                OrderId = payment.OrderId,
                PaymentId = payment.Id,
                RefundId = refund.Id,
                AmountCents = refund.AmountCents,
                Currency = refund.Currency
            })
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new(refund);

    }
    private async Task<RefundResult> SimulateTimeoutMeasuredCore(Guid refundId)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var (refund, payment) = await LoadRefundAndPaymentForOutcome(refundId);

        if (refund is null)
        {
            return new(null, false, "Refund not found");
        }

        if (refund.Status == "Unknown")
        {
            return new(refund, true);
        }

        if (refund.Status != "Pending")
        {
            return new(null, false, "Refund cannot be marked unknown");
        }

        if (payment is null)
        {
            return new(null, false, "There is no payment");
        }

        refund.Status = "Unknown";

        db.Outbox.Add(new OutboxMessage
        {
            OrderId = payment.OrderId,
            Type = "RefundUnknown",
            Payload = JsonSerializer.Serialize(new
            {
                OrderId = payment.OrderId,
                PaymentId = payment.Id,
                RefundId = refund.Id,
                AmountCents = refund.AmountCents,
                Currency = refund.Currency
            })
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return new(refund);

    }

    private async Task<(Refund? Refund, Payment? Payment)> LoadRefundAndPaymentForOutcome(
        Guid refundId)
    {
        if (db.Database.IsSqlServer())
        {
            // Refund creation and all refund outcomes lock the same payment row.
            var paymentId = await db.Refunds.AsNoTracking()
                .Where(r => r.Id == refundId)
                .Select(r => (Guid?)r.PaymentId)
                .SingleOrDefaultAsync();
            if (paymentId is null)
            {
                return (null, null);
            }

            var payment = await db.Payments.FromSqlInterpolated(
                    $"SELECT * FROM dbo.Payments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {paymentId.Value}")
                .SingleOrDefaultAsync();
            var refund = await db.Refunds
                .SingleOrDefaultAsync(r => r.Id == refundId);
            return (refund, payment);
        }

        var existingRefund = await db.Refunds
            .SingleOrDefaultAsync(r => r.Id == refundId);
        var existingPayment = existingRefund is null
            ? null
            : await db.Payments.SingleOrDefaultAsync(p => p.Id == existingRefund.PaymentId);
        return (existingRefund, existingPayment);
    }

    public Task<RefundResult> SimulateTimeout(Guid refundId) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("refund.outcome","sqlserver",()=>SimulateTimeoutMeasuredCore(refundId),r=>r.Error is null?(r.Replayed?"replayed":"success"):"conflict");
    public Task<RefundResult> SimulateFailure(Guid refundId) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("refund.outcome","sqlserver",()=>SimulateFailureMeasuredCore(refundId),r=>r.Error is null?(r.Replayed?"replayed":"success"):"conflict");
    public Task<RefundResult> SimulateSuccess(Guid refundId) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("refund.outcome","sqlserver",()=>SimulateSuccessMeasuredCore(refundId),r=>r.Error is null?(r.Replayed?"replayed":"success"):"conflict");
    public Task<RefundResult> Create(
        Guid paymentId,
        long amountCents,
        string idempotencyKey) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("refund.create","sqlserver",()=>CreateMeasuredCore(paymentId,amountCents,idempotencyKey),r=>r.Error is null?(r.Replayed?"replayed":"success"):"conflict");
}
