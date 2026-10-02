using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Ecommerce;

public record RefundResult(
    Refund? Refund,
    bool Replayed = false,
    string? Error = null
);

public class RefundService(ShopDb db)
{
    public async Task<RefundResult> Create(
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
        await db.Database.OpenConnectionAsync();

        // Use an immediate SQLite transaction so concurrent refund creation
        // attempts cannot both pass validation and create conflicting refunds.
        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);


        // 1. Load the payment that this refund is intended to reverse.
        // The payment provides the original charge amount and currency.
        var payment = await db.Payments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                p => p.Id == paymentId
            );


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
        //    belonging to an order that has already been cancelled.
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


        // 10. Return the newly created refund.
        // throw new NotImplementedException();
    }
    public async Task<RefundResult> SimulateSuccess(Guid refundId)
    {
        await db.Database.OpenConnectionAsync();

        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        var refund = await db.Refunds
            .SingleOrDefaultAsync(r => r.Id == refundId);

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

        // Next: load the payment, update the refund, and record the event.
        var payment = await db.Payments
            .SingleOrDefaultAsync(
                p => p.Id == refund.PaymentId
            );

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
    public async Task<RefundResult> SimulateFailure(Guid refundId)
    {
        await db.Database.OpenConnectionAsync();

        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        var refund = await db.Refunds
            .SingleOrDefaultAsync(r => r.Id == refundId);

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

        // Next: load the payment, update the refund, and record the event.
        var payment = await db.Payments
            .SingleOrDefaultAsync(
                p => p.Id == refund.PaymentId
            );

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
    public async Task<RefundResult> SimulateTimeout(Guid refundId)
    {
        await db.Database.OpenConnectionAsync();

        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        var refund = await db.Refunds
            .SingleOrDefaultAsync(r => r.Id == refundId);

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

        // Next: load the payment, update the refund, and record the event.
        var payment = await db.Payments
            .SingleOrDefaultAsync(
                p => p.Id == refund.PaymentId
            );

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
}