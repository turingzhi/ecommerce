using Microsoft.EntityFrameworkCore;
using System.Text.Json;

using Ecommerce.Dtos;

namespace Ecommerce;

public static partial class Verification
{
    private static async Task VerifyRefundOutcomes()
    {
        await VerifyRefundResolution(false, false);
        await VerifyRefundResolution(true, false);
        await VerifyRefundResolution(true, true);
        await VerifyRefundInvalidRequests();

        foreach (var transition in new[] { "Succeeded", "Failed", "Unknown" })
        {
            await VerifyRefundOutcomeRollback(transition, false);
        }
        await VerifyRefundOutcomeRollback("Succeeded", true);
        await VerifyRefundOutcomeRollback("Failed", true);
        await VerifyConcurrentRefundNotifications("Failed");
        await VerifyConcurrentRefundNotifications("Unknown");
    }

    // Every scenario owns its database. Service calls and assertions use fresh contexts.
    private static async Task WithRefundScenario(Func<RefundScenario, Task> verify)
    {
        var path = $"Verify_ecommerce_refund_outcomes_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDb(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 5 });
                await SaveSeedProducts(db);
            }
            Guid orderId;
            await using (var db = new ShopDb(options))
            {
                var order = await new OrderService(db).Create("customer-a", "checkout",
                    new CreateOrder([new CreateOrderItem(1, 2)]));
                if (order.Error is not null || order.Order is null)
                    throw new Exception("Refund scenario checkout failed");
                orderId = order.Order.Id;
                // A non-default currency makes currency-copy assertions meaningful.
                order.Order.Currency = "USD";
                await db.SaveChangesAsync();
            }
            Guid paymentId;
            await using (var db = new ShopDb(options))
            {
                var payment = await new PaymentService(db).Create("customer-a", orderId, "payment");
                if (payment.Error is not null || payment.Payment is null)
                    throw new Exception("Refund scenario payment creation failed");
                paymentId = payment.Payment.Id;
            }
            await using (var db = new ShopDb(options))
            {
                var paid = await new PaymentService(db).SimulateSuccess(paymentId);
                if (paid.Error is not null) throw new Exception("Refund scenario payment failed");
            }
            Guid refundId;
            await using (var db = new ShopDb(options))
            {
                var refund = await new RefundService(db).Create(paymentId, 5000, "refund-first");
                if (refund.Error is not null || refund.Refund is null)
                    throw new Exception("Refund scenario creation failed");
                refundId = refund.Refund.Id;
            }
            await verify(new RefundScenario(options, orderId, paymentId, refundId));
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }

    private sealed record RefundScenario(
        DbContextOptions<ShopDb> Options, Guid OrderId, Guid PaymentId, Guid RefundId)
    {
        public async Task<RefundResult> Call(Func<RefundService, Task<RefundResult>> action)
        {
            await using var db = new ShopDb(Options);
            return await action(new RefundService(db));
        }

        public Task<RefundResult> Transition(string status) => Call(service => status switch
        {
            "Succeeded" => service.SimulateSuccess(RefundId),
            "Failed" => service.SimulateFailure(RefundId),
            "Unknown" => service.SimulateTimeout(RefundId),
            _ => throw new ArgumentException("Unsupported test transition", nameof(status))
        });

        public async Task AssertState(string status, params string[] eventTypes)
        {
            await using var db = new ShopDb(Options);
            var refund = await db.Refunds.SingleAsync();
            Check(refund.Id == RefundId && refund.PaymentId == PaymentId &&
                  refund.Status == status && refund.AmountCents == 5000 &&
                  refund.Currency == "USD" && refund.IdempotencyKey == "refund-first",
                $"Refund persists as {status} with original identity, amount and currency");
            var payment = await db.Payments.SingleAsync();
            Check(payment.Status == "Succeeded" && payment.AmountCents == 10000 &&
                  payment.Currency == "USD" &&
                  (await db.Orders.SingleAsync()).Status == "Paid" &&
                  (await db.Products.SingleAsync()).Available == 3,
                "Refund outcome leaves payment, order and inventory unchanged");
            var messages = await db.Outbox.ToListAsync();
            var expected = eventTypes.Concat(new[] { "OrderCreated", "OrderPaid" }).OrderBy(t => t);
            Check(messages.Select(m => m.Type).OrderBy(t => t).SequenceEqual(expected),
                "Outbox contains exactly the expected events without duplicates");
            foreach (var message in messages.Where(m => m.Type.StartsWith("Refund")))
            {
                using var payload = JsonDocument.Parse(message.Payload);
                var body = payload.RootElement;
                Check(message.OrderId == OrderId && message.PublishedAt is null &&
                      body.GetProperty("OrderId").GetGuid() == OrderId &&
                      body.GetProperty("PaymentId").GetGuid() == PaymentId &&
                      body.GetProperty("RefundId").GetGuid() == RefundId &&
                      body.GetProperty("AmountCents").GetInt64() == 5000 &&
                      body.GetProperty("Currency").GetString() == "USD",
                    $"{message.Type} payload identifies the refund and its amount/currency");
            }
        }
    }

    private static Task VerifyRefundResolution(bool timedOut, bool succeeds) =>
        WithRefundScenario(async scenario =>
        {
            if (timedOut)
            {
                var timeout = await scenario.Transition("Unknown");
                Check(timeout.Error is null && !timeout.Replayed && timeout.Refund?.Status == "Unknown",
                    "Pending refund becomes Unknown after timeout");
                var replay = await scenario.Transition("Unknown");
                Check(replay.Error is null && replay.Replayed && replay.Refund?.Id == scenario.RefundId,
                    "Repeated refund timeout is a replay");
                var sameKey = await scenario.Call(s => s.Create(scenario.PaymentId, 5000, "refund-first"));
                Check(sameKey.Error is null && sameKey.Replayed && sameKey.Refund?.Status == "Unknown",
                    "Original key replays the Unknown refund");
                var blocked = await scenario.Call(s => s.Create(scenario.PaymentId, 1, "another-refund"));
                Check(blocked.Error == "An unresolved refund already exists for this payment" && blocked.Refund is null,
                    "Unknown refund blocks even a small new attempt");
                await scenario.AssertState("Unknown", "RefundUnknown");
            }

            var status = succeeds ? "Succeeded" : "Failed";
            var result = await scenario.Transition(status);
            Check(result.Error is null && !result.Replayed && result.Refund?.Status == status,
                $"{(timedOut ? "Unknown" : "Pending")} refund resolves to {status}");
            var repeated = await scenario.Transition(status);
            Check(repeated.Error is null && repeated.Replayed && repeated.Refund?.Id == scenario.RefundId,
                $"Repeated {status} refund notification is a replay");

            var lateTimeout = await scenario.Transition("Unknown");
            Check(lateTimeout.Error is not null && lateTimeout.Refund is null && !lateTimeout.Replayed,
                $"Timeout cannot overwrite {status} refund");
            var conflicting = await scenario.Transition(succeeds ? "Failed" : "Succeeded");
            Check(conflicting.Error is not null && conflicting.Refund is null,
                "Conflicting final refund outcome is rejected");
            await scenario.AssertState(status, timedOut
                ? new[] { "RefundUnknown", $"Refund{status}" }
                : new[] { $"Refund{status}" });

            var original = await scenario.Call(s => s.Create(scenario.PaymentId, 5000, "refund-first"));
            Check(original.Error is null && original.Replayed && original.Refund?.Status == status &&
                  original.Refund.Id == scenario.RefundId,
                "Original key replays the resolved refund without creating a retry");

            // Failed attempts reserve no amount; successful attempts consume 5000 cents.
            var remaining = succeeds ? 5000 : 10000;
            var excessive = await scenario.Call(s => s.Create(scenario.PaymentId, remaining + 1, "over-limit"));
            Check(excessive.Error == "Refund amount exceeds the remaining refundable amount" && excessive.Refund is null,
                "Resolved outcome produces the correct remaining refund limit");
            var retry = await scenario.Call(s => s.Create(scenario.PaymentId, remaining, "retry"));
            Check(retry.Error is null && !retry.Replayed && retry.Refund is not null &&
                  retry.Refund.Id != scenario.RefundId && retry.Refund.Status == "Pending" &&
                  retry.Refund.AmountCents == remaining,
                "New key can request exactly the remaining refundable amount");
            await using var db = new ShopDb(scenario.Options);
            var refunds = await db.Refunds.ToListAsync();
            Check(refunds.Count == 2 && refunds.Single(r => r.Id == scenario.RefundId).Status == status &&
                  refunds.Where(r => r.Status != "Failed").Sum(r => r.AmountCents) == 10000,
                "Retry persists without counting failed attempts against the charge");
        });

    private static Task VerifyRefundInvalidRequests() => WithRefundScenario(async scenario =>
    {
        foreach (var status in new[] { "Succeeded", "Failed", "Unknown" })
        {
            var missing = await scenario.Call(s => status switch
            {
                "Succeeded" => s.SimulateSuccess(Guid.NewGuid()),
                "Failed" => s.SimulateFailure(Guid.NewGuid()),
                _ => s.SimulateTimeout(Guid.NewGuid())
            });
            Check(missing.Error == "Refund not found" && missing.Refund is null && !missing.Replayed,
                $"{status} handler rejects missing refund");
        }
        foreach (var key in new[] { "", "  ", new string('x', 101) })
        {
            var invalid = await scenario.Call(s => s.Create(scenario.PaymentId, 100, key));
            var expected = key.Length > 100 ? "Idempotency key must not exceed 100 characters" : "Idempotency key is required";
            Check(invalid.Error == expected && invalid.Refund is null, "Invalid refund key is rejected before creation");
        }
        await scenario.AssertState("Pending");

        // Seed an unsupported state to verify defensive guards, not a normal transition.
        await using (var db = new ShopDb(scenario.Options))
        {
            await db.Refunds.ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "Unsupported"));
        }
        foreach (var status in new[] { "Succeeded", "Failed", "Unknown" })
        {
            var rejected = await scenario.Transition(status);
            Check(rejected.Error is not null && rejected.Refund is null,
                $"{status} handler rejects unsupported refund state");
        }
        await scenario.AssertState("Unsupported");
    });

    private static Task VerifyRefundOutcomeRollback(string target, bool fromUnknown) =>
        WithRefundScenario(async scenario =>
        {
            if (fromUnknown) await scenario.Transition("Unknown");
            await using (var db = new ShopDb(scenario.Options))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE [dbo].[Outbox] WITH NOCHECK
                    ADD CONSTRAINT [CK_VerifyFailRefundOutbox] CHECK ([Type] NOT LIKE 'Refund%')
                    """);
            }
            var failed = false;
            try { await scenario.Transition(target); }
            catch (DbUpdateException) { failed = true; }
            Check(failed, $"{target} handler exposes the injected Outbox storage failure");
            await scenario.AssertState(fromUnknown ? "Unknown" : "Pending",
                fromUnknown ? new[] { "RefundUnknown" } : Array.Empty<string>());
            await using (var db = new ShopDb(scenario.Options))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [CK_VerifyFailRefundOutbox]");
            }
            var retried = await scenario.Transition(target);
            Check(retried.Error is null && !retried.Replayed && retried.Refund?.Status == target,
                "Outcome can be retried after transaction rollback");
            var events = fromUnknown ? new[] { "RefundUnknown", $"Refund{target}" } : new[] { $"Refund{target}" };
            await scenario.AssertState(target, events);
        });

    private static Task VerifyConcurrentRefundNotifications(string target) =>
        WithRefundScenario(async scenario =>
        {
            var results = await Task.WhenAll(
                Task.Run(() => scenario.Transition(target)),
                Task.Run(() => scenario.Transition(target)));
            Check(results.All(r => r.Error is null && r.Refund?.Id == scenario.RefundId) &&
                  results.Count(r => r.Replayed) == 1,
                $"Concurrent {target} notifications apply one transition and one replay");
            await scenario.AssertState(target, $"Refund{target}");
        });
}
