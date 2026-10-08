using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    public static async Task Run()
    {
        await VerifyBasicCheckout();
        await VerifyMultiItemRollback();
        await VerifyMultiItemSuccess();

        await VerifyPaymentCreation();
        await VerifyPaymentFailure();
        await VerifyPaymentTimeout(true);
        await VerifyPaymentTimeout(false);

        await VerifyOrderCancellation();
        await VerifyCancellationWithPayment();
        await VerifyOrderExpiration();
        await VerifyExpirationPaymentSafety();
        await VerifyExpirationWorker();

        await VerifyRefundCreation();
        await VerifyRefundOutcomes();
        await VerifyOutboxDelivery();

        Console.WriteLine("All implemented verification checks passed.");
    }

    #region Checkout scenarios

    private static async Task VerifyBasicCheckout()
    {
        var path = $"Verify_ecommerce_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 1 });
                await SaveSeedProducts(db);
            }
            async Task<OrderResult> Create(string key, int quantity = 1)
            {
                var request = new CreateOrderRequest([new CreateOrderItemRequest(1, quantity)]);
                await using var db = new ShopDbContext(options);
                return await new OrderService(db).Create("customer-a", key, request);
            }

            var outcomes = await Task.WhenAll(
                Task.Run(() => Create("checkout-a")), Task.Run(() => Create("checkout-b")));
            Check(outcomes.Count(r => r.Order is not null) == 1, "Only one checkout gets the last item");
            var winner = outcomes.Single(r => r.Order is not null).Order!;
            var replay = await Create(winner.IdempotencyKey);
            Check(replay.Replayed && replay.Order!.Id == winner.Id, "Retry returns original order");
            Check((await Create(winner.IdempotencyKey, 2)).Error is not null, "Changed payload rejected");

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Outbox.CountAsync() == 1, "Exactly one Outbox record");
                Check((await db.Products.SingleAsync()).Available == 0, "Stock decremented once");
                await db.Products.ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Available, 5).SetProperty(p => p.PriceCents, 6000));
                var order = await db.Orders.Include(o => o.OrderItems).SingleAsync();
                Check(order.OrderItems.Single().UnitPriceCents == 5000, "Historical price unchanged");
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE [dbo].[Outbox] WITH NOCHECK
                    ADD CONSTRAINT [CK_VerifyFailOutbox] CHECK (1 = 0)
                    """);
            }
            var failed = false;
            try { await Create("rollback-test"); }
            catch (DbUpdateException) { failed = true; }
            Check(failed, "Injected storage failure reached caller");
            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 5, "Failure rolls back inventory");
                Check(await db.Orders.CountAsync() == 1, "Failure rolls back order");
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [CK_VerifyFailOutbox]");
            }
            var duplicates = await Task.WhenAll(
                Task.Run(() => Create("same-key")), Task.Run(() => Create("same-key")));
            Check(duplicates.All(r => r.Order is not null) &&
                  duplicates[0].Order!.Id == duplicates[1].Order!.Id &&
                  duplicates.Count(r => r.Replayed) == 1,
                "Concurrent same-key requests create one order");
        }
        finally { await DeleteVerificationDatabase(options); }
    }


    private static async Task VerifyMultiItemRollback()
    {
        // Separate database so this scenario cannot change the existing tests.
        var path = $"Verify_ecommerce_multi_item_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;

        try
        {
            // ARRANGE: Create two products.
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 101, PriceCents = 5000, Available = 5 });
                db.Products.Add(new Product { Id = 102, PriceCents = 2000, Available = 0 });
                await SaveSeedProducts(db);

            }

            // ACT: Try to buy one of each product in ONE order.
            await using (var db = new ShopDbContext(options))
            {
                // TODO 2: Create a CreateOrderRequest request with A first, then B.
                // This makes A's reservation happen before B fails.
                var items = new List<CreateOrderItemRequest>
                {
                    new CreateOrderItemRequest( 101, 1),
                    new CreateOrderItemRequest(102, 1)
                };
                var request = new CreateOrderRequest(items);


                // TODO 3: Call OrderService.Create with this db, customer
                // "customer-a", key "multi-item-rollback", and your request.
                var result = await new OrderService(db).Create("customer-a", "multi-item-rollback", request);

                // TODO 4: Use Check to verify the result has an error
                // and contains no order.
                Check(result.Order == null, "There is no orders");
                Check(result.Error == "Product unavailable or insufficient stock.", "Verify the reuslt has an error");

            }

            // ASSERT: A fresh context checks what actually persisted.
            // The service's context has been disposed before these reads.
            await using (var db = new ShopDbContext(options))
            {
                // TODO 5: Load A and B by their IDs.
                // Check A still has 5 available and B still has 0.
                var prodcut_a = await db.Products.SingleAsync(p => p.Id == 101);
                var prodcut_b = await db.Products.SingleAsync(p => p.Id == 102);
                Check(prodcut_a.Available == 5, "a still has 5 available");
                Check(prodcut_b.Available == 0, "b still has 0 available");

                // TODO 6: Check there are zero Orders, zero OrderItems
                // (use db.Set<OrderItem>()), and zero Outbox messages.
                var orders = await db.Orders.ToListAsync();
                Check(orders.Count == 0, "There is no orders");
                var orderItems = await db.Set<OrderItem>().ToListAsync();
                Check(orderItems.Count == 0, "There is no orderItems");
                var outboxMessage = await db.Outbox.ToListAsync();
                Check(outboxMessage.Count == 0, "There is no outbox message");
            }

            // TODO 7: Remove this throw once every assertion is implemented.
            // Then enable the call above and remove its SKIPPED message.
            // throw new NotImplementedException("Complete the multi-item rollback checks first.");
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private static async Task VerifyMultiItemSuccess()
    {
        var path = $"Verify_ecommerce_success_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;

        try
        {
            // ARRANGE
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();

                // TODO 1: Add product A: Id 101, PriceCents 5000, Available 5.
                db.Products.Add(new Product { Id = 101, PriceCents = 5000, Available = 5 });
                // Add product B: Id 102, PriceCents 2000, Available 3.
                db.Products.Add(new Product { Id = 102, PriceCents = 2000, Available = 3 });
                // Save the products.
                await SaveSeedProducts(db);
            }

            // ACT
            await using (var db = new ShopDbContext(options))
            {
                // TODO 2: Build one CreateOrderRequest requesting 2 of A and 1 of B.
                var items = new List<CreateOrderItemRequest>
                {
                    new CreateOrderItemRequest(101, 2),
                    new CreateOrderItemRequest(102, 1)
                };
                var reqeust = new CreateOrderRequest(items);
                // Call OrderService.Create with customer "customer-a"
                // and idempotency key "multi-item-success".
                var result = await new OrderService(db).Create("customer-a", "multi-item-success", reqeust);

                // TODO 3: Check that result.Error is null and result.Order is not null.
                Check(result.Error == null, "Result Error is null");
                Check(result.Order is not null, "Result Order is not null");
            }

            // ASSERT: Read the persisted data using a fresh context.
            await using (var db = new ShopDbContext(options))
            {
                // TODO 4: Load products by ID. Check A has 3 available and B has 2.
                var prodcut_a = await db.Products.SingleAsync(p => p.Id == 101);
                var prodcut_b = await db.Products.SingleAsync(p => p.Id == 102);
                Check(prodcut_a.Available == 3, "a still has 3 available");
                Check(prodcut_b.Available == 2, "b still has 2 available");

                // TODO 5: Load the single order, including OrderItems.
                // Check that it has exactly two items.
                // Find each item by ProductId (do not assume list ordering).
                // Check A: Quantity 2, UnitPriceCents 5000.
                // Check B: Quantity 1, UnitPriceCents 2000.
                var order = await db.Orders
                    .Include(o => o.OrderItems)
                    .SingleAsync();
                Check(order.OrderItems.Count == 2, "There is 2 orderitems");
                var orderItems = order.OrderItems;
                var orderItemA = orderItems.Single(o => o.ProductId == 101);
                var orderItemB = orderItems.Single(o => o.ProductId == 102);
                Check(orderItemA.Quantity == 2 && orderItemA.UnitPriceCents == 5000, "Check A: Quantity 2, UnitPriceCents 5000.");
                Check(orderItemB.Quantity == 1 && orderItemB.UnitPriceCents == 2000, "Check B: Quantity 1, UnitPriceCents 2000.");


                // TODO 6: Load the single Outbox message.
                // Check its OrderId matches the saved order's Id.
                var outboxMessage = await db.Outbox.SingleAsync();
                Check(outboxMessage.OrderId == order.Id, "Check its OrderId matches the saved order's Id.");
            }

            // TODO 7: Remove this throw when all checks are implemented.
            // Enable the method call in Run() and remove its SKIPPED message.
            // throw new NotImplementedException("Complete the multi-item success checks first.");
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    #endregion

    #region Payment scenarios

    private static async Task VerifyPaymentCreation()
    {
        var path = $"Verify_ecommerce_payments_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        var orderId = Guid.NewGuid();

        try
        {
            // ARRANGE: Seed a saved order directly to focus on payment behavior.
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();

                // Current catalog price differs from the saved purchase price.
                db.Products.Add(new Product { Id = 101, PriceCents = 6000, Available = 5 });
                OrderItem orderItem = new OrderItem
                {
                    ProductId = 101,
                    Quantity = 2,
                    UnitPriceCents = 5000,
                    OrderId = orderId
                };
                db.Orders.Add(new Order
                {
                    Id = orderId,
                    CustomerId = "customer-a",
                    IdempotencyKey = "payment-test-order",
                    Status = "PendingPayment",
                    OrderItems = [orderItem],
                    Currency = "USD"
                });
                // Persist setup before the service runs in a different context.
                await SaveSeedProducts(db);
            }

            // Each call uses a fresh context, like separate HTTP requests.
            async Task<PaymentResult> CreatePayment(string customerId, string key)
            {
                await using var db = new ShopDbContext(options);
                return await new PaymentService(db).Create(customerId, orderId, key);
            }

            // ACT + ASSERT: First creation uses the order's price snapshot and currency.
            var result = await CreatePayment("customer-a", "payment-001");
            Check(result.Error is null && !result.Replayed, "First payment attempt succeeds without replay");
            Check(result.Payment is not null, "Payment is returned");
            var payment = result.Payment!;
            Check(payment.OrderId == orderId && payment.Status == "Pending", "Payment belongs to order and is pending");
            Check(payment.AmountCents == 10000 && payment.Currency == "USD", "Payment uses purchase price and order currency");

            var replay = await CreatePayment("customer-a", "payment-001");
            Check(replay.Error is null && replay.Replayed && replay.Payment?.Id == payment.Id,
                "Same payment key returns original payment");

            var competing = await CreatePayment("customer-a", "payment-002");
            Check(competing.Error is not null && competing.Payment is null,
                "Different key rejected while payment is pending");

            // Ownership is enforced even if another customer knows the original key.
            var otherCustomerReplay = await CreatePayment("customer-b", "payment-001");
            Check(otherCustomerReplay.Error is not null && otherCustomerReplay.Payment is null && !otherCustomerReplay.Replayed,
                "Another customer cannot replay the payment");
            var otherCustomerAttempt = await CreatePayment("customer-b", "payment-b-001");
            Check(otherCustomerAttempt.Error is not null && otherCustomerAttempt.Payment is null,
                "Another customer cannot create a payment for this order");

            // ASSERT: Inspect what actually persisted, using a fresh context.
            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Payments.CountAsync() == 1, "Exactly one payment persisted");
                var savedPayment = await db.Payments.SingleAsync();
                Check(savedPayment.Id == payment.Id && savedPayment.OrderId == orderId &&
                      savedPayment.IdempotencyKey == "payment-001", "Saved payment identity matches original attempt");
                Check(savedPayment.Status == "Pending" && savedPayment.AmountCents == 10000 &&
                      savedPayment.Currency == "USD", "Saved payment retains pending status, amount and currency");
                var savedOrder = await db.Orders.SingleAsync(o => o.Id == orderId);
                Check(savedOrder.Status == "PendingPayment", "Payment creation does not mark order paid");
                Check(!await db.Outbox.AnyAsync(m => m.OrderId == orderId && m.Type == "OrderPaid"),
                    "Payment creation does not publish OrderPaid");
            }
            await using (var db = new ShopDbContext(options))
            {
                var success = await new PaymentService(db)
                    .SimulateSuccess(payment.Id);

                Check(
                    success.Error is null &&
                    !success.Replayed &&
                    success.Payment?.Status == "Succeeded",
                    "Pending payment succeeds"
                );
            }
            await using (var db = new ShopDbContext(options))
            {
                var savedPayment = await db.Payments
                    .SingleAsync(p => p.Id == payment.Id);

                var savedOrder = await db.Orders
                    .SingleAsync(o => o.Id == orderId);

                Check(savedPayment.Status == "Succeeded",
                    "Succeeded payment persisted");

                Check(savedOrder.Status == "Paid",
                    "Paid order persisted");

                Check(await db.Outbox.CountAsync(
                    m => m.OrderId == orderId && m.Type == "OrderPaid") == 1,
                    "Exactly one OrderPaid event persisted");
            }
            await using (var db = new ShopDbContext(options))
            {
                var repeated = await new PaymentService(db)
                    .SimulateSuccess(payment.Id);

                Check(
                    repeated.Error is null &&
                    repeated.Replayed &&
                    repeated.Payment?.Id == payment.Id,
                    "Repeated success returns the same payment as a replay"
                );
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Outbox.CountAsync(
                    m => m.OrderId == orderId && m.Type == "OrderPaid") == 1,
                    "Repeated success does not duplicate OrderPaid");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private static async Task VerifyPaymentFailure()
    {
        var path = $"Verify_ecommerce_payment_failure_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        var orderId = Guid.NewGuid();

        try
        {
            // ARRANGE: Seed a saved order directly to focus on payment behavior.
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();

                db.Products.Add(new Product { Id = 101, PriceCents = 6000, Available = 5 });
                OrderItem orderItem = new OrderItem
                {
                    ProductId = 101,
                    Quantity = 2,
                    UnitPriceCents = 5000,
                    OrderId = orderId
                };
                db.Orders.Add(new Order
                {
                    Id = orderId,
                    CustomerId = "customer-a",
                    IdempotencyKey = "payment-test-order",
                    Status = "PendingPayment",
                    OrderItems = [orderItem],
                    Currency = "USD"
                });
                await SaveSeedProducts(db);
            }

            Guid paymentId;
            await using (var db = new ShopDbContext(options))
            {
                var created = await new PaymentService(db)
                    .Create("customer-a", orderId, "failure-attempt-001");
                Check(created.Error is null && created.Payment?.Status == "Pending",
                    "Failure scenario starts with a Pending payment");
                paymentId = created.Payment!.Id;
            }

            await using (var db = new ShopDbContext(options))
            {
                var failed = await new PaymentService(db).SimulateFailure(paymentId);
                Check(failed.Error is null && !failed.Replayed &&
                      failed.Payment?.Status == "Failed",
                    "Pending payment fails");
            }

            // A fresh context verifies committed database values.
            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Payments.SingleAsync(p => p.Id == paymentId)).Status == "Failed",
                    "Failed payment persisted");
                Check((await db.Orders.SingleAsync(o => o.Id == orderId)).Status == "PendingPayment",
                    "Failed payment leaves order awaiting payment");
                Check((await db.Products.SingleAsync(p => p.Id == 101)).Available == 5,
                    "Payment failure does not change inventory");
                Check(await db.Outbox.CountAsync(m => m.OrderId == orderId && m.Type == "PaymentFailed") == 1,
                    "Exactly one PaymentFailed event persisted");
                Check(!await db.Outbox.AnyAsync(m => m.OrderId == orderId && m.Type == "OrderPaid"),
                    "Payment failure does not emit OrderPaid");
            }

            await using (var db = new ShopDbContext(options))
            {
                var repeated = await new PaymentService(db).SimulateFailure(paymentId);
                Check(repeated.Error is null && repeated.Replayed && repeated.Payment?.Id == paymentId,
                    "Repeated failure returns the same payment as a replay");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Outbox.CountAsync(m => m.OrderId == orderId && m.Type == "PaymentFailed") == 1,
                    "Repeated failure does not duplicate PaymentFailed");
            }

            // Reusing the old key replays the failed attempt; a new key starts a new one.
            await using (var db = new ShopDbContext(options))
            {
                var replay = await new PaymentService(db)
                    .Create("customer-a", orderId, "failure-attempt-001");
                Check(replay.Error is null && replay.Replayed &&
                      replay.Payment?.Id == paymentId && replay.Payment.Status == "Failed",
                    "Original key replays the failed attempt");
            }

            await using (var db = new ShopDbContext(options))
            {
                var retry = await new PaymentService(db)
                    .Create("customer-a", orderId, "failure-attempt-002");
                Check(retry.Error is null && !retry.Replayed && retry.Payment is not null &&
                      retry.Payment.Id != paymentId && retry.Payment.Status == "Pending",
                    "New key allows another payment attempt after failure");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Payments.CountAsync(p => p.OrderId == orderId) == 2 &&
                      await db.Payments.CountAsync(p => p.OrderId == orderId && p.Status == "Pending") == 1,
                    "Retry persists one new Pending attempt alongside the failed attempt");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private static async Task VerifyPaymentTimeout(bool resolvesSuccessfully)
    {
        var path = $"Verify_ecommerce_timeout_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        var orderId = Guid.NewGuid();
        try
        {
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 101, PriceCents = 5000, Available = 5 });
                db.Orders.Add(new Order
                {
                    Id = orderId,
                    CustomerId = "customer-a",
                    IdempotencyKey = "timeout-order",
                    Status = "PendingPayment",
                    Currency = "USD",
                    OrderItems = [new OrderItem
                    {
                        ProductId = 101, Quantity = 2, UnitPriceCents = 5000, OrderId = orderId
                    }]
                });
                await SaveSeedProducts(db);
            }

            // Separate contexts model separate requests and verify committed state.
            async Task<PaymentResult> Call(Func<PaymentService, Task<PaymentResult>> action)
            {
                await using var db = new ShopDbContext(options);
                return await action(new PaymentService(db));
            }

            var created = await Call(service => service.Create("customer-a", orderId, "timeout-001"));
            Check(created.Error is null && created.Payment?.Status == "Pending", "Timeout setup creates Pending payment");
            var paymentId = created.Payment!.Id;
            var missing = await Call(service => service.SimulateTimeout(Guid.NewGuid()));
            Check(missing.Error is not null && missing.Payment is null, "Timeout rejects missing payment");
            var timeout = await Call(service => service.SimulateTimeout(paymentId));
            Check(timeout.Error is null && !timeout.Replayed && timeout.Payment?.Status == "Unknown",
                "Timeout marks payment Unknown");
            var repeated = await Call(service => service.SimulateTimeout(paymentId));
            Check(repeated.Error is null && repeated.Replayed && repeated.Payment?.Id == paymentId,
                "Repeated timeout is a replay");
            var replay = await Call(service => service.Create("customer-a", orderId, "timeout-001"));
            Check(replay.Error is null && replay.Replayed && replay.Payment?.Status == "Unknown" && replay.Payment.Id == paymentId,
                "Same key replays Unknown attempt");
            var blocked = await Call(service => service.Create("customer-a", orderId, "timeout-002"));
            Check(blocked.Error is not null && blocked.Payment is null, "Unknown attempt blocks a new key");

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Payments.CountAsync() == 1 &&
                      (await db.Payments.SingleAsync()).Status == "Unknown", "Only one Unknown payment persisted");
                Check((await db.Orders.SingleAsync()).Status == "PendingPayment", "Timeout leaves order awaiting payment");
                Check((await db.Products.SingleAsync()).Available == 5, "Timeout leaves inventory unchanged");
                Check(await db.Outbox.CountAsync() == 0, "Timeout and replay emit no outcome events");
            }

            var expectedStatus = resolvesSuccessfully ? "Succeeded" : "Failed";
            var expectedEvent = resolvesSuccessfully ? "OrderPaid" : "PaymentFailed";
            var resolved = await Call(service => resolvesSuccessfully
                ? service.SimulateSuccess(paymentId) : service.SimulateFailure(paymentId));
            Check(resolved.Error is null && !resolved.Replayed && resolved.Payment?.Status == expectedStatus,
                $"Unknown payment resolves to {expectedStatus}");
            var resolvedReplay = await Call(service => resolvesSuccessfully
                ? service.SimulateSuccess(paymentId) : service.SimulateFailure(paymentId));
            Check(resolvedReplay.Error is null && resolvedReplay.Replayed && resolvedReplay.Payment?.Id == paymentId,
                $"Repeated {expectedStatus} resolution is a replay");
            var lateTimeout = await Call(service => service.SimulateTimeout(paymentId));
            Check(lateTimeout.Error is not null, "Late timeout cannot overwrite a final outcome");

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Payments.SingleAsync()).Status == expectedStatus, "Final payment outcome persisted");
                Check((await db.Orders.SingleAsync()).Status == (resolvesSuccessfully ? "Paid" : "PendingPayment"),
                    "Resolved order has expected status");
                Check(await db.Outbox.CountAsync() == 1 &&
                      await db.Outbox.CountAsync(m => m.OrderId == orderId && m.Type == expectedEvent) == 1,
                    "Resolution persists exactly one matching event");
                Check((await db.Products.SingleAsync()).Available == 5, "Resolution leaves inventory unchanged");
            }

            var next = await Call(service => service.Create("customer-a", orderId, "timeout-002"));
            Check(resolvesSuccessfully
                    ? next.Error is not null && next.Payment is null
                    : next.Error is null && !next.Replayed && next.Payment?.Status == "Pending" && next.Payment.Id != paymentId,
                "New attempt allowed only after confirmed failure");
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    #endregion

    #region Order cancellation and expiration scenarios

    private static async Task VerifyOrderCancellation()
    {
        var path = $"Verify_ecommerce_cancellation_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 5 });
                await SaveSeedProducts(db);
            }

            Guid orderId;
            await using (var db = new ShopDbContext(options))
            {
                var created = await new OrderService(db).Create(
                    "customer-a", "cancel-checkout", new CreateOrderRequest([new CreateOrderItemRequest(1, 2)]));
                Check(created.Error is null && created.Order is not null,
                    "Cancellation scenario creates a real order");
                orderId = created.Order!.Id;
            }

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 3,
                    "Checkout reserves two units before cancellation");
            }
            await using (var db = new ShopDbContext(options))
            {
                var rejected = await new OrderService(db)
                    .Cancel("customer-b", orderId);

                Check(rejected.Error is not null && rejected.Order is null,
                    "Another customer cannot cancel the order");
            }
            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 3,
                    "Rejected cancellation leaves stock unchanged");

                Check((await db.Orders.SingleAsync(
                    o => o.Id == orderId)).Status == "PendingPayment",
                    "Rejected cancellation leaves order unchanged");

                Check(await db.Outbox.CountAsync() == 1 &&
                    await db.Outbox.AnyAsync(
                        m => m.OrderId == orderId && m.Type == "OrderCreated"),
                    "Rejected cancellation leaves Outbox unchanged");
            }

            await using (var db = new ShopDbContext(options))
            {
                var cancelled = await new OrderService(db).Cancel("customer-a", orderId);
                Check(cancelled.Error is null && !cancelled.Replayed &&
                      cancelled.Order?.Id == orderId && cancelled.Order.Status == "Cancelled",
                    "Owner cancels the unpaid order");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Orders.SingleAsync(o => o.Id == orderId)).Status == "Cancelled",
                    "Cancelled order persisted");
                Check((await db.Products.SingleAsync()).Available == 5,
                    "Cancellation restores reserved stock");
                Check(await db.Outbox.CountAsync(m => m.OrderId == orderId && m.Type == "OrderCancelled") == 1,
                    "Cancellation persists one OrderCancelled event");
            }

            await using (var db = new ShopDbContext(options))
            {
                var repeated = await new OrderService(db).Cancel("customer-a", orderId);
                Check(repeated.Error is null && repeated.Replayed && repeated.Order?.Id == orderId,
                    "Repeated cancellation returns the same order as a replay");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 5,
                    "Repeated cancellation does not restore stock twice");
                Check((await db.Orders.SingleAsync(o => o.Id == orderId)).Status == "Cancelled",
                    "Order remains Cancelled after replay");
                Check(await db.Outbox.CountAsync(m => m.OrderId == orderId && m.Type == "OrderCancelled") == 1,
                    "Repeated cancellation does not duplicate OrderCancelled");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private static async Task VerifyCancellationWithPayment()
    {
        var path = $"Verify_ecommerce_cancel_payment_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 5 });
                await SaveSeedProducts(db);
            }

            Guid orderId;
            await using (var db = new ShopDbContext(options))
            {
                var created = await new OrderService(db).Create(
                    "customer-a", "cancel-payment-checkout", new CreateOrderRequest([new CreateOrderItemRequest(1, 2)]));
                Check(created.Error is null && created.Order is not null,
                    "Cancellation with payment scenario creates a real order");
                orderId = created.Order!.Id;
            }

            // Next: create a Pending payment and verify that cancellation is rejected.
            await using (var db = new ShopDbContext(options))
            {
                var paymentResult = await new PaymentService(db).Create(
                    "customer-a",
                    orderId,
                    "pending-Payment"
                );
                Check(paymentResult.Error is null &&
                paymentResult.Payment?.Status == "Pending",
                "Order has a Pending payment");
                var orderResult = await new OrderService(db).Cancel("customer-a", orderId);
                Check(orderResult.Error is not null && orderResult.Order is null,
                "Pending payment blocks cancellation");
            }
            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 3,
                    "Blocked cancellation keeps stock reserved");

                Check((await db.Orders.SingleAsync()).Status == "PendingPayment",
                    "Blocked cancellation leaves order unchanged");

                Check((await db.Payments.SingleAsync()).Status == "Pending",
                    "Blocked cancellation leaves payment unchanged");

                Check(await db.Outbox.CountAsync() == 1 &&
                      await db.Outbox.AnyAsync(m => m.Type == "OrderCreated"),
                    "Blocked cancellation leaves Outbox unchanged");
            }
            await using (var db = new ShopDbContext(options))
            {
                var payment = await db.Payments.SingleAsync();

                var timeout = await new PaymentService(db)
                    .SimulateTimeout(payment.Id);

                Check(timeout.Error is null &&
                      timeout.Payment?.Status == "Unknown",
                    "Payment becomes Unknown before cancellation");
            }

            await using (var db = new ShopDbContext(options))
            {
                var rejected = await new OrderService(db)
                    .Cancel("customer-a", orderId);

                Check(rejected.Error is not null && rejected.Order is null,
                    "Unknown payment blocks cancellation");
            }
            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 3,
                    "Unknown payment cancellation keeps stock reserved");

                Check((await db.Orders.SingleAsync()).Status == "PendingPayment",
                    "Unknown payment cancellation leaves order unchanged");

                Check((await db.Payments.SingleAsync()).Status == "Unknown",
                    "Rejected cancellation preserves Unknown payment");

                Check(await db.Outbox.CountAsync() == 1 &&
                      await db.Outbox.AnyAsync(m => m.Type == "OrderCreated"),
                    "Unknown payment cancellation leaves Outbox unchanged");
            }
            await using (var db = new ShopDbContext(options))
            {
                var payment = await db.Payments.SingleAsync();

                var success = await new PaymentService(db)
                    .SimulateSuccess(payment.Id);

                Check(success.Error is null &&
                      success.Payment?.Status == "Succeeded",
                    "Payment succeeds before paid-order cancellation");
            }
            await using (var db = new ShopDbContext(options))
            {
                var rejected = await new OrderService(db)
                    .Cancel("customer-a", orderId);

                Check(rejected.Error is not null && rejected.Order is null,
                    "Paid order cannot be cancelled");
            }
            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Orders.SingleAsync()).Status == "Paid",
                    "Rejected cancellation leaves order Paid");

                Check((await db.Payments.SingleAsync()).Status == "Succeeded",
                    "Rejected cancellation leaves payment Succeeded");

                Check((await db.Products.SingleAsync()).Available == 3,
                    "Paid-order cancellation does not restore stock");

                Check(await db.Outbox.CountAsync() == 2 &&
                      await db.Outbox.CountAsync(m => m.Type == "OrderCreated") == 1 &&
                      await db.Outbox.CountAsync(m => m.Type == "OrderPaid") == 1,
                    "Paid-order cancellation leaves Outbox unchanged");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private static async Task VerifyOrderExpiration()
    {
        var path = $"Verify_ecommerce_expiration_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 5 });
                await SaveSeedProducts(db);
            }

            Guid orderId;
            DateTime createdAt;
            await using (var db = new ShopDbContext(options))
            {
                var created = await new OrderService(db).Create(
                    "customer-a", "expiration-checkout",
                    new CreateOrderRequest([new CreateOrderItemRequest(1, 2)]));
                Check(created.Error is null && created.Order is not null,
                    "Expiration scenario creates an order");
                orderId = created.Order!.Id;
                createdAt = created.Order.CreatedAt;
            }

            // Supply the time directly so this test does not need to wait.
            await using (var db = new ShopDbContext(options))
            {
                var result = await new OrderService(db).Expire(
                    orderId, createdAt.AddMinutes(15).AddSeconds(-1));
                Check(result.Error is not null && result.Order is null,
                    "Order cannot expire before 15 minutes");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 3,
                    "Early expiration keeps stock reserved");
                Check((await db.Orders.SingleAsync()).Status == "PendingPayment",
                    "Early expiration leaves order unchanged");
                Check(await db.Outbox.CountAsync() == 1 &&
                      await db.Outbox.AnyAsync(m => m.OrderId == orderId && m.Type == "OrderCreated"),
                    "Early expiration leaves Outbox unchanged");
            }
            await using (var db = new ShopDbContext(options))
            {
                var expired = await new OrderService(db).Expire(
                    orderId, createdAt.AddMinutes(15));

                Check(expired.Error is null &&
                      !expired.Replayed &&
                      expired.Order?.Status == "Cancelled",
                    "Order expires at exactly 15 minutes");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Orders.SingleAsync()).Status == "Cancelled",
                    "Expired order persists as Cancelled");

                Check((await db.Products.SingleAsync()).Available == 5,
                    "Expiration restores reserved stock");

                Check(await db.Outbox.CountAsync(
                    m => m.OrderId == orderId &&
                         m.Type == "OrderCancelled") == 1,
                    "Expiration persists one cancellation event");
            }
            await using (var db = new ShopDbContext(options))
            {
                var repeated = await new OrderService(db).Expire(
                    orderId, createdAt.AddMinutes(16));

                Check(repeated.Error is null &&
                      repeated.Replayed &&
                      repeated.Order?.Id == orderId,
                    "Repeated expiration returns the same order as a replay");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check((await db.Products.SingleAsync()).Available == 5,
                    "Repeated expiration does not restore stock twice");

                Check((await db.Orders.SingleAsync()).Status == "Cancelled",
                    "Order remains Cancelled after repeated expiration");

                Check(await db.Outbox.CountAsync(
                    m => m.OrderId == orderId &&
                         m.Type == "OrderCancelled") == 1,
                    "Repeated expiration does not duplicate the event");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    #endregion

    #region Refund scenarios

    private static async Task VerifyRefundCreation()
    {
        var path = $"Verify_ecommerce_refunds_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 5 });
                await SaveSeedProducts(db);
            }

            Guid orderId;
            await using (var db = new ShopDbContext(options))
            {
                var created = await new OrderService(db).Create(
                    "customer-a", "refund-checkout",
                    new CreateOrderRequest([new CreateOrderItemRequest(1, 2)]));
                Check(created.Error is null && created.Order is not null,
                    "Refund scenario creates a real order");
                orderId = created.Order!.Id;
            }

            Guid paymentId;
            await using (var db = new ShopDbContext(options))
            {
                var created = await new PaymentService(db)
                    .Create("customer-a", orderId, "refund-payment");
                Check(created.Error is null && created.Payment?.AmountCents == 10000,
                    "Refund scenario creates a payment for 10000 cents");
                paymentId = created.Payment!.Id;
            }

            await using (var db = new ShopDbContext(options))
            {
                var success = await new PaymentService(db).SimulateSuccess(paymentId);
                Check(success.Error is null && success.Payment?.Status == "Succeeded",
                    "Refund scenario completes payment successfully");
            }

            await using (var db = new ShopDbContext(options))
            {
                var excessive = await new RefundService(db)
                    .Create(paymentId, 10001, "refund-too-large");

                Check(excessive.Error ==
                          "Refund amount exceeds the remaining refundable amount" &&
                      excessive.Refund is null,
                    "Refund exceeding the original payment is rejected");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 0,
                    "Excessive refund does not create a record");
            }
            foreach (var amount in new long[] { 0, -1 })
            {
                await using var db = new ShopDbContext(options);

                var invalid = await new RefundService(db)
                    .Create(paymentId, amount, $"invalid-amount-{amount}");

                Check(invalid.Error == "Refund amount must be positive" &&
                      invalid.Refund is null,
                    $"Refund amount {amount} is rejected");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 0,
                    "Invalid amounts do not create refund records");
            }
            Guid refundId;
            await using (var db = new ShopDbContext(options))
            {
                var result = await new RefundService(db)
                    .Create(paymentId, 5000, "refund-001");
                Check(result.Error is null && !result.Replayed && result.Refund?.Status == "Pending",
                    "First refund request creates a Pending refund");
                refundId = result.Refund!.Id;
            }

            // Read committed state in a fresh context, not the service's tracked objects.
            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 1, "Exactly one refund persisted");
                var refund = await db.Refunds.SingleAsync();
                var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
                Check(refund.Id == refundId && refund.PaymentId == paymentId &&
                      refund.IdempotencyKey == "refund-001", "Saved refund identifies its payment and request");
                Check(refund.Status == "Pending" && refund.AmountCents == 5000 &&
                      refund.Currency == payment.Currency, "Saved refund retains status, amount and payment currency");
                Check(payment.Status == "Succeeded" && payment.AmountCents == 10000,
                    "Refund creation leaves the original payment unchanged");
                Check((await db.Orders.SingleAsync(o => o.Id == orderId)).Status == "Paid",
                    "Refund creation leaves the order Paid");
                Check((await db.Products.SingleAsync()).Available == 3,
                    "Refund creation does not restore inventory");
                Check(await db.Outbox.CountAsync() == 2 &&
                      await db.Outbox.CountAsync(m => m.Type == "OrderCreated") == 1 &&
                      await db.Outbox.CountAsync(m => m.Type == "OrderPaid") == 1,
                    "Pending refund creation leaves Outbox unchanged");
            }
            await using (var db = new ShopDbContext(options))
            {
                var replay = await new RefundService(db)
                    .Create(paymentId, 5000, "refund-001");

                Check(replay.Error is null &&
                      replay.Replayed &&
                      replay.Refund?.Id == refundId,
                    "Same refund key and amount returns the original refund");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 1,
                    "Refund replay does not create another refund");
            }
            await using (var db = new ShopDbContext(options))
            {
                var conflict = await new RefundService(db)
                    .Create(paymentId, 3000, "refund-001");

                Check(conflict.Error is not null &&
                      conflict.Refund is null &&
                      !conflict.Replayed,
                    "Same refund key with a different amount is rejected");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 1,
                    "Conflicting request does not create another refund");

                var saved = await db.Refunds.SingleAsync();

                Check(saved.Id == refundId && saved.AmountCents == 5000,
                    "Conflicting request leaves the original refund unchanged");
            }
            await using (var db = new ShopDbContext(options))
            {
                var blocked = await new RefundService(db)
                    .Create(paymentId, 3000, "refund-002");

                Check(blocked.Error is not null &&
                      blocked.Refund is null &&
                      !blocked.Replayed,
                    "Pending refund blocks another refund attempt");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 1,
                    "Blocked attempt does not create another refund");
                var saved = await db.Refunds.SingleAsync();
                Check(saved.Id == refundId && saved.Status == "Pending" && saved.AmountCents == 5000,
                    "Blocked attempt leaves the Pending refund unchanged");
            }

            // 1. Complete the Pending refund.
            await using (var db = new ShopDbContext(options))
            {
                var success = await new RefundService(db)
                    .SimulateSuccess(refundId);

                Check(success.Error is null &&
                      !success.Replayed &&
                      success.Refund?.Id == refundId &&
                      success.Refund.Status == "Succeeded",
                    "Pending refund succeeds");
            }

            // 2. Verify the committed state using a fresh context.
            await using (var db = new ShopDbContext(options))
            {
                var refund = await db.Refunds
                    .SingleAsync(r => r.Id == refundId);

                Check(refund.Status == "Succeeded" &&
                      refund.AmountCents == 5000,
                    "Succeeded refund and amount persisted");

                Check(await db.Outbox.CountAsync(
                    m => m.OrderId == orderId &&
                         m.Type == "RefundSucceeded") == 1,
                    "Exactly one RefundSucceeded event persisted");

                Check((await db.Payments.SingleAsync(
                    p => p.Id == paymentId)).Status == "Succeeded",
                    "Refund success preserves the original payment status");

                Check((await db.Orders.SingleAsync(
                    o => o.Id == orderId)).Status == "Paid",
                    "Partial refund leaves the order Paid");

                Check((await db.Products.SingleAsync()).Available == 3,
                    "Refund success does not restore inventory");
            }

            // 3. Simulate receiving the same success notification again.
            await using (var db = new ShopDbContext(options))
            {
                var repeated = await new RefundService(db)
                    .SimulateSuccess(refundId);

                Check(repeated.Error is null &&
                      repeated.Replayed &&
                      repeated.Refund?.Id == refundId &&
                      repeated.Refund.Status == "Succeeded",
                    "Repeated refund success returns the original refund as a replay");
            }

            // 4. Verify replay did not duplicate the refund or event.
            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 1,
                    "Repeated success does not create another refund");

                Check((await db.Refunds.SingleAsync()).Status == "Succeeded",
                    "Refund remains Succeeded after replay");

                Check(await db.Outbox.CountAsync(
                    m => m.OrderId == orderId &&
                         m.Type == "RefundSucceeded") == 1,
                    "Repeated success does not duplicate RefundSucceeded");

                Check((await db.Products.SingleAsync()).Available == 3,
                    "Repeated refund success leaves inventory unchanged");
            }

            // 5. A completed partial refund reduces the remaining refundable amount.
            await using (var db = new ShopDbContext(options))
            {
                var excessive = await new RefundService(db)
                    .Create(paymentId, 5001, "refund-over-remaining");
                Check(excessive.Error == "Refund amount exceeds the remaining refundable amount" &&
                      excessive.Refund is null && !excessive.Replayed,
                    "Completed partial refund prevents refunding more than the remaining 5000 cents");
            }

            await using (var db = new ShopDbContext(options))
            {
                Check(await db.Refunds.CountAsync() == 1 &&
                      (await db.Refunds.SingleAsync()).AmountCents == 5000,
                    "Excessive second refund leaves existing records unchanged");
            }

            // 6. Exactly the remaining amount is allowed with a new key.
            Guid secondRefundId;
            await using (var db = new ShopDbContext(options))
            {
                var remaining = await new RefundService(db)
                    .Create(paymentId, 5000, "refund-002");
                Check(remaining.Error is null && !remaining.Replayed && remaining.Refund is not null &&
                      remaining.Refund.Id != refundId && remaining.Refund.Status == "Pending" &&
                      remaining.Refund.AmountCents == 5000,
                    "A new refund can use exactly the remaining 5000 cents");
                secondRefundId = remaining.Refund!.Id;
            }

            await using (var db = new ShopDbContext(options))
            {
                var refunds = await db.Refunds.Where(r => r.PaymentId == paymentId).ToListAsync();
                Check(refunds.Count == 2 && refunds.Sum(r => r.AmountCents) == 10000 &&
                      refunds.Single(r => r.Id == refundId).Status == "Succeeded" &&
                      refunds.Single(r => r.Id == secondRefundId).Status == "Pending",
                    "Completed and Pending refunds reserve exactly the original charge");
            }

            // 7. Complete the second refund, then verify the charge is fully refunded.
            await using (var db = new ShopDbContext(options))
            {
                var success = await new RefundService(db).SimulateSuccess(secondRefundId);
                Check(success.Error is null && !success.Replayed && success.Refund?.Status == "Succeeded",
                    "Remaining refund completes successfully");
            }

            await using (var db = new ShopDbContext(options))
            {
                var exhausted = await new RefundService(db)
                    .Create(paymentId, 1, "refund-after-full");
                Check(exhausted.Error == "Refund amount exceeds the remaining refundable amount" &&
                      exhausted.Refund is null,
                    "Fully refunded payment rejects even one additional cent");
            }

            await using (var db = new ShopDbContext(options))
            {
                var replay = await new RefundService(db).Create(paymentId, 5000, "refund-001");
                Check(replay.Error is null && replay.Replayed && replay.Refund?.Id == refundId,
                    "Original request still replays after the charge is fully refunded");
            }

            await using (var db = new ShopDbContext(options))
            {
                var refunds = await db.Refunds.Where(r => r.PaymentId == paymentId).ToListAsync();
                Check(refunds.Count == 2 && refunds.All(r => r.Status == "Succeeded") &&
                      refunds.Sum(r => r.AmountCents) == 10000,
                    "Total successful refunds equal the original charge without exceeding it");
                var events = await db.Outbox
                    .Where(m => m.OrderId == orderId && m.Type == "RefundSucceeded").ToListAsync();
                var eventRefundIds = new List<Guid>();
                foreach (var message in events)
                {
                    using var payload = System.Text.Json.JsonDocument.Parse(message.Payload);
                    eventRefundIds.Add(payload.RootElement.GetProperty("RefundId").GetGuid());
                }
                Check(eventRefundIds.Count == 2 && eventRefundIds.Contains(refundId) &&
                      eventRefundIds.Contains(secondRefundId),
                    "Each completed refund has exactly one success event");
                Check((await db.Products.SingleAsync()).Available == 3 &&
                      (await db.Orders.SingleAsync()).Status == "Paid" &&
                      (await db.Payments.SingleAsync()).Status == "Succeeded",
                    "Refund completion leaves inventory and original order/payment states unchanged");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    #endregion

    #region Shared assertion helper

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"FAIL: {name}");
        Console.WriteLine($"PASS: {name}");
    }

    #endregion
}
