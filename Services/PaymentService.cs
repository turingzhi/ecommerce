using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
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
        await db.Database.OpenConnectionAsync();

        // SQLite's immediate transaction serializes writers so two new attempts
        // cannot both pass the pending-payment check and insert a payment.
        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        // Load the order and its price snapshots, enforcing ownership in the query.

        var order = await db.Orders
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync(
                o => o.Id == orderId &&
                o.CustomerId == customerId
            );

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
        // TODO: Copy order.Currency into this payment as well.
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
        await db.Database.OpenConnectionAsync();

        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        var payment = await db.Payments.SingleOrDefaultAsync(
            p => p.Id == paymentId
        );
        if (payment is null)
        {
            return new(null, false, "Payment not found");
        }

        var order = await db.Orders.SingleOrDefaultAsync(
            o => o.Id == payment.OrderId);

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
        await db.Database.OpenConnectionAsync();

        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);

        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        var payment = await db.Payments.SingleOrDefaultAsync(
            p => p.Id == paymentId
        );
        if (payment is null)
        {
            return new(null, false, "Payment not found");
        }

        var order = await db.Orders.SingleOrDefaultAsync(
            o => o.Id == payment.OrderId);

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
        await db.Database.OpenConnectionAsync();
        await using var transaction =
            ((SqliteConnection)db.Database.GetDbConnection())
            .BeginTransaction(deferred: false);
        await using var enlisted =
            await db.Database.UseTransactionAsync(transaction);

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Id == paymentId);
        if (payment is null)
        {
            return new(null, false, "Payment not found");
        }

        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == payment.OrderId);
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
    // public async Task<PaymentResult> SimulateLateSuccess(Guid paymentId)
    // {
        
    // }

}
