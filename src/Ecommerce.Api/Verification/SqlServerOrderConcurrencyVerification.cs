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
    private static async Task VerifySqlServerConcurrentCheckoutReplay(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var customerId = $"sql-checkout-replay-{Guid.NewGuid():N}";
            int productId;
            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Concurrent checkout replay product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]);

            async Task<(OrderResult? Result, Exception? Failure)> CreateOrderRequest()
            {
                await start.Task;
                try
                {
                    await using var db = new ShopDbContext(options);
                    return (await new OrderService(db).Create(
                        customerId, "same-key", request), null);
                }
                catch (Exception exception)
                {
                    return (null, exception);
                }
            }

            var first = CreateOrderRequest();
            var second = CreateOrderRequest();
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var orders = await checkDb.Orders.AsNoTracking()
                .Where(o => o.CustomerId == customerId)
                .ToListAsync();
            var stock = await checkDb.Products.AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => p.Available)
                .SingleAsync();
            Guid? savedOrderId = orders.FirstOrDefault()?.Id;
            var events = await checkDb.Outbox
                .CountAsync(m => m.OrderId == savedOrderId && m.Type == "OrderCreated");

            Check(results.All(r => r.Failure is null &&
                    r.Result is { Error: null, Order: not null })
                && results.Count(r => r.Result!.Replayed) == 1
                && orders.Count == 1
                && results.All(r => r.Result!.Order!.Id == orders[0].Id)
                && stock == 0 && events == 1,
                $"Concurrent same-key checkout replays one order " +
                $"(attempt {attempt + 1}, errors={string.Join(",", results.Select(r => r.Failure?.GetType().Name ?? r.Result?.Error))})");
        }
    }

    private static async Task VerifySqlServerCompetingCheckout(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            int productId;
            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Last item checkout product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<OrderResult> CreateOrderRequest(string customerId)
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Create(
                    customerId, "last-item",
                    new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
            }

            var first = CreateOrderRequest($"last-item-a-{Guid.NewGuid():N}");
            var second = CreateOrderRequest($"last-item-b-{Guid.NewGuid():N}");
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var stock = await checkDb.Products.AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => p.Available)
                .SingleAsync();
            var orderCount = await checkDb.Set<OrderItem>()
                .CountAsync(item => item.ProductId == productId);
            Check(results.Count(r => r.Order is not null && r.Error is null) == 1
                && results.Count(r => r.Order is null && r.Error is not null) == 1
                && orderCount == 1 && stock == 0,
                $"Competing SQL Server checkouts cannot oversell the last item " +
                $"(attempt {attempt + 1}, stock={stock}, items={orderCount})");
        }
    }

    private static async Task VerifySqlServerPaymentCancellationRace(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-cancel-payment-{Guid.NewGuid():N}";
            int productId;
            Guid orderId;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Payment cancellation race product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;

                var created = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
                Check(created.Order is not null && created.Error is null,
                    "Payment cancellation race creates an order");
                orderId = created.Order!.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<PaymentResult> CreatePayment()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new PaymentService(db)
                    .Create(customerId, orderId, "payment-001");
            }

            async Task<OrderResult> CancelOrder()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Cancel(customerId, orderId);
            }

            var paymentTask = CreatePayment();
            var cancellationTask = CancelOrder();
            start.SetResult();
            var payment = await paymentTask;
            var cancellation = await cancellationTask;

            await using var checkDb = new ShopDbContext(options);
            var order = await checkDb.Orders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId);
            var stock = await checkDb.Products.AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => p.Available)
                .SingleAsync();
            var paymentCount = await checkDb.Payments
                .CountAsync(p => p.OrderId == orderId);
            var cancellationEvents = await checkDb.Outbox
                .CountAsync(m => m.OrderId == orderId && m.Type == "OrderCancelled");

            var paymentWon = payment.Payment is not null && payment.Error is null
                && cancellation.Order is null && cancellation.Error is not null
                && order.Status == "PendingPayment" && paymentCount == 1
                && stock == 0 && cancellationEvents == 0;
            var cancellationWon = cancellation.Order is not null && cancellation.Error is null
                && payment.Payment is null && payment.Error is not null
                && order.Status == "Cancelled" && paymentCount == 0
                && stock == 1 && cancellationEvents == 1;

            Check(paymentWon || cancellationWon,
                $"Payment/cancellation race (attempt {attempt + 1}): " +
                $"payment={payment.Payment?.Status ?? payment.Error}, " +
                $"cancellation={cancellation.Order?.Status ?? cancellation.Error}, " +
                $"order={order.Status}, payments={paymentCount}, stock={stock}, " +
                $"cancellation events={cancellationEvents}");
        }
    }

    private static async Task VerifySqlServerPaymentExpirationRace(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-expire-payment-{Guid.NewGuid():N}";
            int productId;
            Guid orderId;
            DateTime overdueTime;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Payment expiration race product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;

                var created = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
                Check(created.Order is not null && created.Error is null,
                    "Payment expiration race creates an order");
                orderId = created.Order!.Id;
                overdueTime = created.Order.CreatedAt.AddMinutes(16);
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<PaymentResult> CreatePayment()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new PaymentService(db)
                    .Create(customerId, orderId, "payment-001");
            }

            async Task<OrderResult> ExpireOrder()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Expire(orderId, overdueTime);
            }

            var paymentTask = CreatePayment();
            var expirationTask = ExpireOrder();
            start.SetResult();
            var payment = await paymentTask;
            var expiration = await expirationTask;

            await using var checkDb = new ShopDbContext(options);
            var order = await checkDb.Orders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId);
            var stock = await checkDb.Products.AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => p.Available)
                .SingleAsync();
            var paymentCount = await checkDb.Payments
                .CountAsync(p => p.OrderId == orderId);
            var cancellationEvents = await checkDb.Outbox
                .CountAsync(m => m.OrderId == orderId && m.Type == "OrderCancelled");

            var paymentWon = payment.Payment is not null && payment.Error is null
                && expiration.Order is null && expiration.Error is not null
                && order.Status == "PendingPayment" && paymentCount == 1
                && stock == 0 && cancellationEvents == 0;
            var expirationWon = expiration.Order is not null && expiration.Error is null
                && payment.Payment is null && payment.Error is not null
                && order.Status == "Cancelled" && paymentCount == 0
                && stock == 1 && cancellationEvents == 1;

            Check(paymentWon || expirationWon,
                $"Payment/expiration race (attempt {attempt + 1}): " +
                $"payment={payment.Payment?.Status ?? payment.Error}, " +
                $"expiration={expiration.Order?.Status ?? expiration.Error}, " +
                $"order={order.Status}, payments={paymentCount}, stock={stock}, " +
                $"cancellation events={cancellationEvents}");
        }
    }

    private static async Task VerifySqlServerConcurrentExpiration(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-double-expire-{Guid.NewGuid():N}";
            int productId;
            Guid orderId;
            DateTime overdueTime;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Double expiration verification product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;

                var created = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
                Check(created.Order is not null && created.Error is null,
                    "Double expiration test creates an order");
                orderId = created.Order!.Id;
                overdueTime = created.Order.CreatedAt.AddMinutes(16);
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<OrderResult> ExpireOrder()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Expire(orderId, overdueTime);
            }

            var firstTask = ExpireOrder();
            var secondTask = ExpireOrder();
            start.SetResult();
            var results = await Task.WhenAll(firstTask, secondTask);

            await using var checkDb = new ShopDbContext(options);
            var order = await checkDb.Orders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId);
            var stock = await checkDb.Products.AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => p.Available)
                .SingleAsync();
            var cancellationEvents = await checkDb.Outbox
                .CountAsync(m => m.OrderId == orderId && m.Type == "OrderCancelled");

            Check(results.All(r => r.Error is null && r.Order is not null)
                && results.Count(r => !r.Replayed) == 1
                && results.Count(r => r.Replayed) == 1
                && order.Status == "Cancelled"
                && stock == 1 && cancellationEvents == 1,
                $"Concurrent expiration restores stock and emits event once " +
                $"(attempt {attempt + 1}, stock={stock}, events={cancellationEvents})");
        }
    }

    private static async Task VerifySqlServerCancellationExpirationRace(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-cancel-expire-{Guid.NewGuid():N}";
            int productId;
            Guid orderId;
            DateTime overdueTime;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Cancellation expiration race product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;

                var created = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
                Check(created.Order is not null && created.Error is null,
                    "Cancellation expiration race creates an order");
                orderId = created.Order!.Id;
                overdueTime = created.Order.CreatedAt.AddMinutes(16);
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<OrderResult> CancelOrder()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Cancel(customerId, orderId);
            }

            async Task<OrderResult> ExpireOrder()
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Expire(orderId, overdueTime);
            }

            var cancelTask = CancelOrder();
            var expireTask = ExpireOrder();
            start.SetResult();
            var results = await Task.WhenAll(cancelTask, expireTask);

            await using var checkDb = new ShopDbContext(options);
            var order = await checkDb.Orders.AsNoTracking()
                .SingleAsync(o => o.Id == orderId);
            var stock = await checkDb.Products.AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => p.Available)
                .SingleAsync();
            var cancellationEvents = await checkDb.Outbox
                .CountAsync(m => m.OrderId == orderId && m.Type == "OrderCancelled");

            Check(results.All(r => r.Error is null && r.Order is not null)
                && results.Count(r => !r.Replayed) == 1
                && results.Count(r => r.Replayed) == 1
                && order.Status == "Cancelled"
                && stock == 1 && cancellationEvents == 1,
                $"Cancellation and expiration restore stock once " +
                $"(attempt {attempt + 1}, stock={stock}, events={cancellationEvents})");
        }
    }
}
