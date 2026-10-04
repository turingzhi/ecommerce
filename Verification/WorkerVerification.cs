using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecommerce;

public static partial class Verification
{
    private static async Task VerifyExpirationWorker()
    {
        await VerifyExpirationWorkerSelection();
        await VerifyExpirationWorkerFailureIsolation();
    }

    private static async Task WithExpirationWorker(
        Func<DbContextOptions<ShopDb>, OrderExpirationWorker, Task> verify)
    {
        var path = $"Verify_ecommerce_worker_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<ShopDb>(o => o.UseSqlServer(VerificationConnection(path)));
            services.AddScoped<OrderService>();
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            await using (var db = new ShopDb(options))
            {
                await db.Database.EnsureCreatedAsync();
                // Fixtures represent inventory already reserved by saved orders.
                db.Products.Add(new Product { Id = 1, PriceCents = 100, Available = 0 });
                await SaveSeedProducts(db);
            }
            using var worker = new OrderExpirationWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OrderExpirationWorker>.Instance);
            await verify(options, worker);
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private static Order WorkerOrder(DateTime createdAt, string status = "PendingPayment") => new()
    {
        CustomerId = "worker-customer",
        IdempotencyKey = Guid.NewGuid().ToString(),
        CreatedAt = createdAt,
        Status = status,
        OrderItems = [new OrderItem { ProductId = 1, Quantity = 1, UnitPriceCents = 100 }]
    };

    private static Task VerifyExpirationWorkerSelection() => WithExpirationWorker(async (options, worker) =>
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var eligible = new List<Guid>();
        var blocked = new List<Guid>();
        Guid youngId;
        Guid paidId;
        Guid cancelledId;
        await using (var db = new ShopDb(options))
        {
            // More than a batch of older blocked orders must not starve eligible ones.
            for (var i = 0; i < 102; i++)
            {
                var order = WorkerOrder(now.AddHours(-2));
                db.Orders.Add(order);
                blocked.Add(order.Id);
                db.Payments.Add(new Payment
                {
                    OrderId = order.Id, AmountCents = 100, IdempotencyKey = "blocked",
                    Status = i % 2 == 0 ? "Pending" : "Unknown"
                });
            }
            // The 101st eligible order sits exactly on the expiration boundary.
            for (var i = 0; i < 101; i++)
            {
                var order = WorkerOrder(now.AddMinutes(-15).AddSeconds(i - 100));
                db.Orders.Add(order);
                eligible.Add(order.Id);
                if (i == 0)
                    db.Payments.Add(new Payment
                    {
                        OrderId = order.Id, AmountCents = 100,
                        IdempotencyKey = "failed", Status = "Failed"
                    });
            }
            var young = WorkerOrder(now.AddMinutes(-15).AddSeconds(1));
            var paid = WorkerOrder(now.AddHours(-3), "Paid");
            var cancelled = WorkerOrder(now.AddHours(-3), "Cancelled");
            db.Orders.AddRange(young, paid, cancelled);
            youngId = young.Id;
            paidId = paid.Id;
            cancelledId = cancelled.Id;
            await db.SaveChangesAsync();
        }

        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            var cancelled = false;
            try { await worker.RunBatchAsync(now, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Worker respects cancellation before starting a batch");
        }
        await using (var db = new ShopDb(options))
        {
            Check((await db.Products.SingleAsync()).Available == 0 && await db.Outbox.CountAsync() == 0,
                "Cancelled batch changes no stock or events");
        }

        await worker.RunBatchAsync(now);
        await using (var db = new ShopDb(options))
        {
            var expired = await db.Orders.Where(o => eligible.Contains(o.Id) && o.Status == "Cancelled")
                .Select(o => o.Id).ToListAsync();
            Check(expired.Count == 100 && eligible.Take(100).All(expired.Contains),
                "Worker expires the oldest 100 eligible orders despite older blocked orders");
            Check((await db.Orders.SingleAsync(o => o.Id == eligible[100])).Status == "PendingPayment",
                "Worker leaves the 101st eligible order for another batch");
            Check((await db.Products.SingleAsync()).Available == 100 && await db.Outbox.CountAsync() == 100,
                "First batch restores one unit and records one event per order");
        }

        await worker.RunBatchAsync(now);
        await worker.RunBatchAsync(now);
        await using (var db = new ShopDb(options))
        {
            Check(await db.Orders.CountAsync(o => eligible.Contains(o.Id) && o.Status == "Cancelled") == 101,
                "Next batch expires the remaining order at exactly 15 minutes");
            Check(await db.Orders.CountAsync(o => blocked.Contains(o.Id) && o.Status == "PendingPayment") == 102,
                "Worker leaves all Pending/Unknown payment orders untouched");
            Check(await db.Payments.CountAsync(p => p.Status == "Pending") == 51 &&
                  await db.Payments.CountAsync(p => p.Status == "Unknown") == 51 &&
                  await db.Payments.CountAsync(p => p.Status == "Failed") == 1,
                "Worker preserves payment statuses, including failed attempts");
            Check((await db.Orders.SingleAsync(o => o.Id == youngId)).Status == "PendingPayment" &&
                  (await db.Orders.SingleAsync(o => o.Id == paidId)).Status == "Paid" &&
                  (await db.Orders.SingleAsync(o => o.Id == cancelledId)).Status == "Cancelled",
                "Worker skips young, Paid and already Cancelled orders");
            var events = await db.Outbox.ToListAsync();
            Check(events.Count == 101 && events.All(e => e.Type == "OrderCancelled") &&
                  events.Select(e => e.OrderId).Distinct().Count() == 101 &&
                  events.All(e => e.OrderId.HasValue && eligible.Contains(e.OrderId.Value)),
                "Repeated batches produce exactly one cancellation event per eligible order");
            Check((await db.Products.SingleAsync()).Available == 101,
                "Repeated worker batches never restore stock twice");
        }
    });

    private static Task VerifyExpirationWorkerFailureIsolation() => WithExpirationWorker(async (options, worker) =>
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Guid firstId;
        Guid secondId;
        await using (var db = new ShopDb(options))
        {
            var first = WorkerOrder(now.AddMinutes(-30));
            var second = WorkerOrder(now.AddMinutes(-20));
            firstId = first.Id;
            secondId = second.Id;
            db.Orders.AddRange(first, second);
            await db.SaveChangesAsync();
            // Fail only the oldest order, after its stock update but before commit.
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] WITH NOCHECK ADD CONSTRAINT " +
                $"[CK_VerifyFailOldestExpiration] CHECK " +
                $"([Type] <> 'OrderCancelled' OR [OrderId] <> '{firstId:D}')");
        }
        await worker.RunBatchAsync(now);
        await using (var db = new ShopDb(options))
        {
            Check((await db.Orders.SingleAsync(o => o.Id == firstId)).Status == "PendingPayment" &&
                  !await db.Outbox.AnyAsync(e => e.OrderId == firstId),
                "Failed expiration rolls back its order and event");
            Check((await db.Orders.SingleAsync(o => o.Id == secondId)).Status == "Cancelled" &&
                  await db.Outbox.CountAsync(e => e.OrderId == secondId) == 1,
                "Worker continues to the next order after an individual failure");
            Check((await db.Products.SingleAsync()).Available == 1,
                "Fresh scope isolates failed stock restoration from the next order");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [CK_VerifyFailOldestExpiration]");
        }
        await worker.RunBatchAsync(now);
        await worker.RunBatchAsync(now);
        await using (var db = new ShopDb(options))
        {
            Check(await db.Orders.CountAsync(o => o.Status == "Cancelled") == 2 &&
                  (await db.Products.SingleAsync()).Available == 2 &&
                  await db.Outbox.CountAsync() == 2,
                "Later batch retries the failed order without duplicating successful expiration");
        }
    });
}
