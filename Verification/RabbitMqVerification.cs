using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Ecommerce;

public static partial class Verification
{
    public static async Task RunRabbitMq()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecommerce-rabbitmq-{Guid.NewGuid()}.db");
        var dbOptions = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        var rabbitOptions = Options.Create(new RabbitMqOptions());
        var publisher = new RabbitMqEventPublisher(rabbitOptions);
        using var services = new ServiceCollection()
            .AddDbContext<ShopDb>(options => options.UseSqlite($"Data Source={path};Pooling=False"))
            .AddScoped<EventConsumer>()
            .BuildServiceProvider();
        using var worker = new RabbitMqConsumerWorker(rabbitOptions,
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RabbitMqConsumerWorker>.Instance);

        try
        {
            OutboxMessage message;
            await using (var db = new ShopDb(dbOptions))
            {
                await db.Database.EnsureCreatedAsync();
                var order = new Order
                {
                    CustomerId = "rabbitmq-verification",
                    IdempotencyKey = Guid.NewGuid().ToString("N")
                };
                message = new OutboxMessage
                {
                    OrderId = order.Id,
                    Type = "OrderCreated",
                    Payload = "{}"
                };
                db.Orders.Add(order);
                db.Outbox.Add(message);
                await db.SaveChangesAsync();
            }

            await worker.StartAsync(CancellationToken.None);
            await using (var db = new ShopDb(dbOptions))
                await new OutboxDispatcher(db, publisher).DispatchBatchAsync(CancellationToken.None);

            await WaitUntilAsync(async () =>
            {
                await using var db = new ShopDb(dbOptions);
                return await db.ProcessedMessages.AnyAsync(p => p.MessageId == message.Id);
            }, "RabbitMQ consumer did not process confirmed publication");
            await using (var db = new ShopDb(dbOptions))
            {
                Check((await db.Outbox.SingleAsync()).PublishedAt is not null,
                    "Outbox marks event published after broker confirmation");
                Check(await db.ProcessedMessages.CountAsync() == 1,
                    "RabbitMQ consumer saves processed ID before acknowledgement");
            }

            await publisher.PublishAsync(message, CancellationToken.None);
            await Task.Delay(500);
            await using (var db = new ShopDb(dbOptions))
                Check(await db.ProcessedMessages.CountAsync() == 1,
                    "Repeated RabbitMQ delivery has no duplicate database effect");

            var unroutablePublisher = new RabbitMqEventPublisher(Options.Create(
                new RabbitMqOptions { RoutingKey = "missing.binding" }));
            var unroutableRejected = false;
            try { await unroutablePublisher.PublishAsync(message, CancellationToken.None); }
            catch (RabbitMQ.Client.Exceptions.PublishException) { unroutableRejected = true; }
            Check(unroutableRejected,
                "Mandatory unroutable publish fails instead of confirming delivery");

            // An unreachable broker must leave a new Outbox record available for retry.
            await using (var db = new ShopDb(dbOptions))
            {
                db.Outbox.Add(new OutboxMessage
                {
                    OrderId = message.OrderId,
                    Type = "OrderCreated",
                    Payload = "{}"
                });
                await db.SaveChangesAsync();
            }
            var unavailablePublisher = new RabbitMqEventPublisher(
                Options.Create(new RabbitMqOptions { Port = 1 }));
            var brokerFailure = false;
            try
            {
                await using var db = new ShopDb(dbOptions);
                await new OutboxDispatcher(db, unavailablePublisher)
                    .DispatchBatchAsync(CancellationToken.None);
            }
            catch (Exception) { brokerFailure = true; }
            await using (var db = new ShopDb(dbOptions))
            {
                var pending = await db.Outbox.SingleAsync(m => m.Id != message.Id);
                Check(brokerFailure && pending.PublishedAt is null && pending.AttemptCount == 1,
                    "Broker outage keeps Outbox event pending with retry metadata");
                pending.AttemptCount = 4;
                pending.NextAttemptAt = null;
                await db.SaveChangesAsync();
            }
            try
            {
                await using var db = new ShopDb(dbOptions);
                await new OutboxDispatcher(db, unavailablePublisher)
                    .DispatchBatchAsync(CancellationToken.None);
            }
            catch (BrokerDeliveryUnavailableException) { }
            await using (var db = new ShopDb(dbOptions))
            {
                var pending = await db.Outbox.SingleAsync(m => m.Id != message.Id);
                Check(pending.AttemptCount == 5 && !pending.DeadLettered,
                    "Long broker outage does not permanently dead-letter a recoverable event");
                pending.NextAttemptAt = null;
                await db.SaveChangesAsync();
            }
            await using (var db = new ShopDb(dbOptions))
                await new OutboxDispatcher(db, publisher).DispatchBatchAsync(CancellationToken.None);
            await using (var db = new ShopDb(dbOptions))
                Check((await db.Outbox.SingleAsync(m => m.Id != message.Id)).PublishedAt is not null,
                    "Outbox event publishes after broker recovery");

            // Fail the consumer's DB write once, then remove the fault before retry.
            await using (var db = new ShopDb(dbOptions))
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER FailRabbitConsumer BEFORE INSERT ON ProcessedMessages
                    BEGIN SELECT RAISE(ABORT, 'simulated consumer database outage'); END;
                    """);
            var retryMessage = new OutboxMessage
            {
                OrderId = message.OrderId,
                Type = "OrderPaid",
                Payload = "{}"
            };
            var retriesBefore = await QueueMessageCountAsync(RabbitMqTopology.RetryQueue);
            var deadBeforeDatabaseOutage = await QueueMessageCountAsync(RabbitMqTopology.DeadQueue);
            var factory = RabbitMqTopology.CreateFactory(rabbitOptions.Value);
            await using (var connection = await factory.CreateConnectionAsync())
            await using (var channel = await connection.CreateChannelAsync(
                RabbitMqTopology.ConfirmedChannelOptions()))
            {
                await channel.BasicPublishAsync(RabbitMqTopology.Exchange,
                    RabbitMqTopology.RoutingKey, mandatory: true,
                    basicProperties: new BasicProperties
                    {
                        Persistent = true,
                        MessageId = retryMessage.Id.ToString("D"),
                        ContentType = "application/json",
                        Headers = new Dictionary<string, object?> { ["x-attempts"] = 4 }
                    }, body: JsonSerializer.SerializeToUtf8Bytes(new BrokerEvent(
                        retryMessage.Id, retryMessage.OrderId, retryMessage.Type,
                        retryMessage.Payload)));
            }
            await WaitUntilAsync(async () => await QueueMessageCountAsync(
                RabbitMqTopology.RetryQueue) > retriesBefore,
                "Temporary consumer database outage at attempt four did not reach retry queue");
            await using (var db = new ShopDb(dbOptions))
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailRabbitConsumer;");
            await WaitUntilAsync(async () =>
            {
                await using var db = new ShopDb(dbOptions);
                return await db.ProcessedMessages.AnyAsync(p => p.MessageId == retryMessage.Id);
            }, "Consumer did not recover after database failure");
            Check(await QueueMessageCountAsync(RabbitMqTopology.DeadQueue) == deadBeforeDatabaseOutage,
                "Temporary consumer database outage does not dead-letter the event");
            Check(true, "Failed consumer write is retried and processed after recovery");

            // Poison messages have a bounded number of attempts and end in the DLQ.
            var poisonId = Guid.NewGuid().ToString("D");
            var deadBefore = await QueueMessageCountAsync(RabbitMqTopology.DeadQueue);
            await using (var connection = await factory.CreateConnectionAsync())
            await using (var channel = await connection.CreateChannelAsync(
                RabbitMqTopology.ConfirmedChannelOptions()))
            {
                await channel.BasicPublishAsync(RabbitMqTopology.Exchange,
                    RabbitMqTopology.RoutingKey, mandatory: true,
                    basicProperties: new BasicProperties
                    {
                        Persistent = true,
                        MessageId = poisonId,
                        ContentType = "application/json"
                    }, body: Encoding.UTF8.GetBytes("not-json"));
            }
            await WaitUntilAsync(async () => await QueueMessageCountAsync(
                RabbitMqTopology.DeadQueue) > deadBefore,
                "Poison event did not reach dead-letter queue", TimeSpan.FromSeconds(20));
            await using (var connection = await factory.CreateConnectionAsync())
            await using (var channel = await connection.CreateChannelAsync())
            {
                BasicGetResult? dead = null;
                for (var i = 0; i <= deadBefore; i++)
                {
                    var candidate = await channel.BasicGetAsync(
                        RabbitMqTopology.DeadQueue, autoAck: false);
                    if (candidate is null) break;
                    if (candidate.BasicProperties.MessageId == poisonId)
                    {
                        dead = candidate;
                        break;
                    }
                    // Other messages remain unacknowledged and return to the queue
                    // when this channel closes.
                }
                Check(dead is not null &&
                      ReadDeadLetterAttempts(dead.BasicProperties.Headers) ==
                      RabbitMqTopology.MaximumAttempts,
                    "Poison event reaches dead-letter queue after five attempts");
                await channel.BasicAckAsync(dead!.DeliveryTag, false);
            }

            Console.WriteLine("RabbitMQ verification passed.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(path);
        }
    }

    private static int ReadDeadLetterAttempts(IDictionary<string, object?>? headers) =>
        headers is not null && headers.TryGetValue("x-attempts", out var value) &&
        value is int attempts ? attempts : -1;

    private static async Task<uint> QueueMessageCountAsync(string queue)
    {
        var factory = RabbitMqTopology.CreateFactory(new RabbitMqOptions());
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, string failure, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(12));
        while (DateTime.UtcNow < limit)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new Exception(failure);
    }
}
