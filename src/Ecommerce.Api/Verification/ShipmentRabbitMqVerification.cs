using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Messaging.Outbox;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static async Task VerifyRabbitMqShipmentRecoveryAsync()
    {
        var path = $"Verify_ecommerce_shipment_broker_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(VerificationConnection(path)).Options;
        var rabbitOptions = Options.Create(new RabbitMqOptions());
        var publisher = new RabbitMqEventPublisher(rabbitOptions);
        using var services = new ServiceCollection()
            .AddDbContext<ShopDbContext>(o => o.UseSqlServer(VerificationConnection(path)))
            .AddScoped<EventConsumer>().BuildServiceProvider();
        using var worker = new RabbitMqConsumerWorker(rabbitOptions,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<RabbitMqConsumerWorker>.Instance);
        try
        {
            await using (var db = new ShopDbContext(options)) await db.Database.MigrateAsync();
            var fixture = await CreatePaidShipmentFixtureAsync(options);
            await using (var db = new ShopDbContext(options))
            {
                await new OutboxDispatcher(db, publisher).DispatchBatchAsync(CancellationToken.None);
                Check((await db.Outbox.SingleAsync(m => m.Id == fixture.PaidEvent.Id)).PublishedAt is not null &&
                    !await db.Shipments.AnyAsync(), "Paid event confirmed while shipment consumer is stopped");
            }
            await worker.StartAsync(CancellationToken.None);
            await WaitUntilAsync(async () =>
            {
                await using var db = new ShopDbContext(options);
                return await db.Shipments.AnyAsync(s => s.OrderId == fixture.OrderId) &&
                    await db.ProcessedMessages.AnyAsync(m => m.MessageId == fixture.PaidEvent.Id);
            }, "Delayed paid event did not create shipment");
            Check(true, "Consumer restart creates delayed shipment and marker");
            await publisher.PublishAsync(fixture.PaidEvent, CancellationToken.None);
            var another = CopyPaidEvent(fixture);
            await publisher.PublishAsync(another, CancellationToken.None);
            await WaitUntilAsync(async () =>
            {
                await using var db = new ShopDbContext(options);
                return await db.ProcessedMessages.AnyAsync(m => m.MessageId == another.Id);
            }, "Paid event replay was not consumed");
            await using var read = new ShopDbContext(options);
            Check(await read.ShipmentHistory.CountAsync()==1,"Broker paid-event replays retain exactly one initial history entry");
            Check(await read.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1 &&
                await read.ProcessedMessages.CountAsync(m => m.MessageId == another.Id || m.MessageId == fixture.PaidEvent.Id) == 2,
                "Broker replays retain one shipment and both paid-event markers");
        }
        finally
        {
            try { await worker.StopAsync(CancellationToken.None); }
            finally { await DeleteVerificationDatabase(options); }
        }
    }
}
