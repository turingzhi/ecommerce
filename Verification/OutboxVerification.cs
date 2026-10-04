using Microsoft.EntityFrameworkCore;

namespace Ecommerce;

public static partial class Verification
{
    private sealed class RecordingPublisher : IEventPublisher
    {
        public bool Fail { get; set; }
        public List<Guid> Delivered { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException("Simulated publisher failure");
            Delivered.Add(message.Id);
            return Task.CompletedTask;
        }
    }

    private static async Task VerifyOutboxDelivery()
    {
        await VerifyConsumerDeduplication();
        foreach (var failureMode in new[] { "none", "publisher", "marker" })
        {
            var path = $"Verify_ecommerce_outbox_{Guid.NewGuid():N}";
            var options = new DbContextOptionsBuilder<ShopDb>()
                .UseSqlServer(VerificationConnection(path)).Options;
            try
            {
                Guid messageId;
                await using (var db = new ShopDb(options))
                {
                    await db.Database.EnsureCreatedAsync();
                    var order = new Order { CustomerId = "customer-a", IdempotencyKey = "outbox-test" };
                    db.Orders.Add(order);
                    var message = new OutboxMessage { OrderId = order.Id, Type = "OrderCreated", Payload = "{}" };
                    messageId = message.Id;
                    db.Outbox.Add(message);
                    await db.SaveChangesAsync();
                    if (failureMode == "marker")
                    {
                        await db.Database.ExecuteSqlRawAsync("""
                            ALTER TABLE [dbo].[Outbox] WITH NOCHECK
                            ADD CONSTRAINT [CK_VerifyFailPublishedMarker] CHECK ([PublishedAt] IS NULL)
                            """);
                    }
                }

                var publisher = new RecordingPublisher { Fail = failureMode == "publisher" };
                async Task Dispatch()
                {
                    // A failed batch must dispose its context before retrying.
                    await using var db = new ShopDb(options);
                    await new OutboxDispatcher(db, publisher).DispatchBatchAsync(CancellationToken.None);
                }

                var threw = false;
                try { await Dispatch(); }
                catch (InvalidOperationException ex) when (ex.Message == "Simulated publisher failure") { threw = true; }
                catch (DbUpdateException) when (failureMode == "marker") { threw = true; }
                Check(threw == (failureMode != "none"), $"Outbox {failureMode} scenario reports expected outcome");
                Check(publisher.Delivered.Count == (failureMode == "publisher" ? 0 : 1),
                    "Publisher records only successful delivery calls");

                await using (var db = new ShopDb(options))
                {
                    var saved = await db.Outbox.SingleAsync();
                    Check((saved.PublishedAt is not null) == (failureMode == "none"),
                        "Publication marker persists only after delivery and database save succeed");
                    Check(saved.Id == messageId && saved.Payload == "{}" && saved.Type == "OrderCreated",
                        "Dispatch preserves event identity and content");
                    if (failureMode == "marker")
                        await db.Database.ExecuteSqlRawAsync(
                            "ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [CK_VerifyFailPublishedMarker]");
                }

                publisher.Fail = false;
                if (failureMode != "none")
                {
                    // Attempt 1 schedules a two-second backoff.
                    await Task.Delay(TimeSpan.FromSeconds(2.2));
                }
                await Dispatch();
                await Dispatch();
                var expectedDeliveries = failureMode == "marker" ? 2 : 1;
                Check(publisher.Delivered.Count == expectedDeliveries &&
                      publisher.Delivered.All(id => id == messageId),
                    failureMode == "marker"
                        ? "Marker failure causes duplicate delivery with the same stable message ID"
                        : "Retry delivers unpublished events and skips published events");
                await using (var db = new ShopDb(options))
                {
                    Check((await db.Outbox.SingleAsync()).PublishedAt is not null,
                        "Successful delivery eventually persists its publication marker");
                }
            }
            finally
            {
                await DeleteVerificationDatabase(options);
            }
        }
    }

    private static async Task VerifyConsumerDeduplication()
    {
        var path = $"Verify_ecommerce_consumer_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            Guid messageId;
            await using (var db = new ShopDb(options))
            {
                await db.Database.EnsureCreatedAsync();
                var order = new Order { CustomerId = "consumer-test", IdempotencyKey = "consumer-order" };
                db.Orders.Add(order);
                var message = new OutboxMessage
                {
                    OrderId = order.Id,
                    Type = "OrderCreated",
                    Payload = "{}"
                };
                messageId = message.Id;
                db.Outbox.Add(message);
                await db.SaveChangesAsync();
            }

            bool first;
            bool second;
            await using (var db = new ShopDb(options))
            {
                var consumer = new EventConsumer(db);
                var message = await db.Outbox.SingleAsync(m => m.Id == messageId);
                first = await consumer.ConsumeAsync(message, CancellationToken.None);
            }
            await using (var db = new ShopDb(options))
            {
                var consumer = new EventConsumer(db);
                var message = await db.Outbox.SingleAsync(m => m.Id == messageId);
                second = await consumer.ConsumeAsync(message, CancellationToken.None);
            }

            Check(first && !second,
                "Consumer handles the first message and ignores its duplicate");
            await using (var db = new ShopDb(options))
            {
                Check(await db.ProcessedMessages.CountAsync() == 1 &&
                      (await db.ProcessedMessages.SingleAsync()).MessageId == messageId,
                    "Consumer persists exactly one processed message ID");
            }
        }
        finally
        {
            await DeleteVerificationDatabase(options);
        }
    }
}
