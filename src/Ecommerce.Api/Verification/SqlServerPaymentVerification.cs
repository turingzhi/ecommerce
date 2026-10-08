using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static async Task VerifySqlServerConcurrentPaymentReplay(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-concurrent-replay-{Guid.NewGuid():N}";
            Guid orderId;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Concurrent replay verification product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();

                var order = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
                Check(order.Order is not null && order.Error is null,
                    "Concurrent replay test creates an order");
                orderId = order.Order!.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<PaymentResult> CreatePayment()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new PaymentService(db)
                    .Create(customerId, orderId, "payment-same-key");
            }

            var first = CreatePayment();
            var second = CreatePayment();
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var stored = await checkDb.Payments.CountAsync(p => p.OrderId == orderId);
            Check(results.All(r => r.Error is null && r.Payment is not null)
                && results[0].Payment!.Id == results[1].Payment!.Id
                && results.Count(r => r.Replayed) == 1
                && stored == 1,
                $"Concurrent same-key payments replay one stored attempt (attempt {attempt + 1})");
        }
    }

    private static async Task VerifySqlServerConcurrentPayment(
        DbContextOptions<ShopDbContext> options)
    {
        // Repeat with fresh orders so an unlucky scheduler cannot hide the race.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-concurrent-payment-{Guid.NewGuid():N}";
            Guid orderId;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Concurrent payment verification product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();

                var order = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
                Check(order.Order is not null && order.Error is null,
                    "Concurrent payment test creates an order");
                orderId = order.Order!.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<PaymentResult> CreatePayment(string key)
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new PaymentService(db).Create(customerId, orderId, key);
            }

            var first = CreatePayment("payment-a");
            var second = CreatePayment("payment-b");
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var stored = await checkDb.Payments.CountAsync(p => p.OrderId == orderId);
            Check(results.Count(r => r.Payment is not null && r.Error is null) == 1
                && results.Count(r => r.Payment is null && r.Error is not null) == 1
                && stored == 1,
                $"Concurrent different-key payments: one accepted, one rejected, one stored (attempt {attempt + 1})");
        }
    }

    private static async Task VerifySqlServerPayment(
        DbContextOptions<ShopDbContext> options)
    {
        var customerId = $"sql-payment-{Guid.NewGuid():N}";
        var product = new Product
        {
            Name = "Payment verification product",
            PriceCents = 5000,
            Available = 10
        };

        await using (var db = new ShopDbContext(options))
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
        }

        Guid orderId;

        await using (var db = new ShopDbContext(options))
        {
            var order = await new OrderService(db).Create(
                customerId,
                "checkout-001",
                new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 2)]));

            Check(order.Error is null && order.Order is not null,
                "Payment test creates an order");
            orderId = order.Order!.Id;
        }

        // A later price change must not change this order's payment amount.
        await using (var db = new ShopDbContext(options))
        {
            await db.Products.Where(p => p.Id == product.Id)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(p => p.PriceCents, 6000));
        }

        async Task<PaymentResult> CreatePayment(string key)
        {
            await using var db = new ShopDbContext(options);
            return await new PaymentService(db)
                .Create(customerId, orderId, key);
        }

        async Task<PaymentResult> SucceedPayment(Guid paymentId)
        {
            await using var db = new ShopDbContext(options);
            return await new PaymentService(db)
                .SimulateSuccess(paymentId);
        }

        var first = await CreatePayment("payment-001");

        Check(first.Error is null && !first.Replayed
            && first.Payment?.Status == "Pending",
            "Payment starts Pending");

        var paymentId = first.Payment!.Id;

        Check(first.Payment.AmountCents == 10000
            && first.Payment.Currency == "EUR",
            "Payment uses saved purchase prices and order currency");

        var replay = await CreatePayment("payment-001");
        Check(replay.Error is null && replay.Replayed
            && replay.Payment?.Id == paymentId,
            "Same key returns the same payment");

        var blocked = await CreatePayment("payment-002");
        Check(blocked.Payment is null
            && blocked.Error == "An unresolved payment already exists for this order",
            "Different key is blocked while payment is Pending");

        await using (var db = new ShopDbContext(options))
        {
            Check((await db.Payments.SingleAsync(p => p.Id == paymentId))
                .Status == "Pending",
                "Pending payment is persisted");

            Check((await db.Orders.SingleAsync(o => o.Id == orderId))
                .Status == "PendingPayment",
                "Creating a payment does not mark the order Paid");

            Check(!await db.Outbox.AnyAsync(m =>
                m.OrderId == orderId && m.Type == "OrderPaid"),
                "Pending payment creates no OrderPaid event");
        }

        var success = await SucceedPayment(paymentId);
        Check(success.Error is null && !success.Replayed
            && success.Payment?.Status == "Succeeded",
            "Payment success is accepted");

        var successReplay = await SucceedPayment(paymentId);
        Check(successReplay.Error is null && successReplay.Replayed
            && successReplay.Payment?.Id == paymentId,
            "Repeated payment success is a replay");

        await using (var db = new ShopDbContext(options))
        {
            Check((await db.Payments.SingleAsync(p => p.Id == paymentId))
                .Status == "Succeeded",
                "Succeeded payment is persisted");

            Check((await db.Orders.SingleAsync(o => o.Id == orderId))
                .Status == "Paid",
                "Paid order is persisted");

            Check(await db.Payments.CountAsync(p => p.OrderId == orderId) == 1,
                "Only one payment is stored");

            Check((await db.Products.SingleAsync(p => p.Id == product.Id))
                .Available == 8,
                "Payment does not reserve stock again");

            Check(await db.Outbox.CountAsync(m =>
                m.OrderId == orderId && m.Type == "OrderPaid") == 1,
                "Exactly one OrderPaid event is stored");
        }
    }
}
