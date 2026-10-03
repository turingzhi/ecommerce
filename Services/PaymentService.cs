using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Ecommerce;

public record PaymentResult(Payment? Payment, bool Replayed = false, string? Error = null);

public class PaymentService(ShopDb db)
{
    public async Task<PaymentResult> Create(
        string customerId,
        Guid orderId,
        string idempotencyKey)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        // Serialize payment creation per order on SQL Server. Keep the lock until
        // the pending-payment check and insert commit in this transaction.
        var orders = db.Database.IsSqlServer()
            ? db.Orders.FromSqlInterpolated(
                $"SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {orderId} AND CustomerId = {customerId}")
            : db.Orders.Where(o => o.Id == orderId && o.CustomerId == customerId);

        var order = await orders
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync();

        if (order is null)
        {
            return new(null, false, "There is no order");
        }


        // Replay the same attempt only after verifying ownership.
        var payment_check = await db.Payments
            .SingleOrDefaultAsync(
                p => p.OrderId == orderId
                && p.IdempotencyKey == idempotencyKey
        );
        if (payment_check is not null)
        {
            return new(payment_check, true);
        }

        // A new attempt requires an order that is still awaiting payment.
        if (order.Status != "PendingPayment")
        {
            return new(null, false, "This order cannot be paid");
        }

        // Calculate the amount from saved purchase prices, not current product prices.
        long amount = 0;
        foreach (var item in order.OrderItems)
        {
            amount += item.UnitPriceCents * item.Quantity;
        }

        // Reject a different key while another attempt is pending for this order.
        var result = await db.Payments.AnyAsync(
            p => p.OrderId == orderId &&
            (p.Status == "Pending" || p.Status == "Unknown")
        );
        if (result)
        {
            return new(null, false, "An unresolved payment already exists for this order");
        }
        // Create a Pending attempt; the Payment model supplies the initial status.
        var payment = new Payment
        {
            OrderId = orderId,
            AmountCents = amount,
            IdempotencyKey = idempotencyKey,
            Currency = order.Currency
        };
        db.Payments.Add(payment);


        // Persist the transaction's changes together; early returns roll back on disposal.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        // Return the newly created payment attempt.
        return new(payment);
    }

    public async Task<PaymentResult> SimulateSuccess(
        Guid paymentId
    )
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var (payment, order) = await LoadPaymentAndOrderForOutcome(paymentId);
        if (payment is null)
        {
            return new(null, false, "Payment not found");
        }

        if (order is null)
        {
            return new(null, false, "Order not found");
        }
        if (payment.Status == "Succeeded")
        {
            return new(payment, true);
        }
        if (payment.Status != "Pending" && payment.Status != "Unknown")
        {
            return new(null, false, "Payment cannot be done");
        }

        if (order.Status != "PendingPayment")
        {
            return new(null, false, "Order cannot be done");
        }
        payment.Status = "Succeeded";

        order.Status = "Paid";

        db.Outbox.Add(new OutboxMessage
        {
            OrderId = order.Id,
            Type = "OrderPaid",
            Payload = JsonSerializer.Serialize(new
            {
                OrderId = order.Id,
                PaymentId = payment.Id,
                CustomerId = order.CustomerId,
                AmountCents = payment.AmountCents,
                Currency = payment.Currency
            })
        });


        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new PaymentResult(payment);
        
    }
     public async Task<PaymentResult> SimulateFailure(
        Guid paymentId
    )
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var (payment, order) = await LoadPaymentAndOrderForOutcome(paymentId);
        if (payment is null)
        {
            return new(null, false, "Payment not found");
        }

        if (order is null)
        {
            return new(null, false, "Order not found");
        }
        if (payment.Status == "Failed")
        {
            return new(payment, true);
        }

        if (payment.Status != "Pending" && payment.Status != "Unknown")
        {
            return new(null, false, "Payment cannot be marked failed");
        }

        if (order.Status != "PendingPayment")
        {
            return new(null, false, "Order is not awaiting payment");
        }

        payment.Status = "Failed";



        db.Outbox.Add(new OutboxMessage
        {
            OrderId = order.Id,
            Type = "PaymentFailed",
            Payload = JsonSerializer.Serialize(new
            {
                OrderId = order.Id,
                PaymentId = payment.Id,
                CustomerId = order.CustomerId,
                AmountCents = payment.AmountCents,
                Currency = payment.Currency
            })
        });


        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new PaymentResult(payment);
        
    }

    public async Task<PaymentResult> SimulateTimeout(Guid paymentId)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var (payment, order) = await LoadPaymentAndOrderForOutcome(paymentId);
        if (payment is null)
        {
            return new(null, false, "Payment not found");
        }
        if (order is null)
        {
            return new(null, false, "Order not found");
        }
        if (payment.Status == "Unknown")
        {
            return new(payment, true);
        }
        if (payment.Status != "Pending")
        {
            return new(null, false, "Payment cannot be marked unknown");
        }
        if (order.Status != "PendingPayment")
        {
            return new(null, false, "Order is not awaiting payment");
        }

        // Timeout leaves the outcome uncertain; do not emit a failure event.
        payment.Status = "Unknown";
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(payment);
    }

    private async Task<(Payment? Payment, Order? Order)> LoadPaymentAndOrderForOutcome(
        Guid paymentId)
    {
        if (db.Database.IsSqlServer())
        {
            // Read the stable foreign key first, then lock the order before
            // checking payment state. All order/payment transitions use this lock.
            var orderId = await db.Payments.AsNoTracking()
                .Where(p => p.Id == paymentId)
                .Select(p => (Guid?)p.OrderId)
                .SingleOrDefaultAsync();
            if (orderId is null)
            {
                return (null, null);
            }

            var order = await db.Orders.FromSqlInterpolated(
                    $"SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {orderId.Value}")
                .SingleOrDefaultAsync();
            var payment = await db.Payments
                .SingleOrDefaultAsync(p => p.Id == paymentId);
            return (payment, order);
        }

        var existingPayment = await db.Payments
            .SingleOrDefaultAsync(p => p.Id == paymentId);
        var existingOrder = existingPayment is null
            ? null
            : await db.Orders.SingleOrDefaultAsync(o => o.Id == existingPayment.OrderId);
        return (existingPayment, existingOrder);
    }
}
