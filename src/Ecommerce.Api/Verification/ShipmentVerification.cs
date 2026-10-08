using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    public static async Task RunShipments()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection($"Verify_ecommerce_shipments_{Guid.NewGuid():N}")).Options;
        try
        {
            await using (var db = new ShopDbContext(options)) await db.Database.MigrateAsync();
            await VerifyShipmentSchemaAsync(options);
            await VerifyShipmentDeliveryAsync(options);
            await VerifyShipmentConcurrentDeliveryAsync(options);
            await VerifyShipmentValidationAsync(options);
            await VerifyShipmentRollbackAsync(options);
            await VerifyShipmentStatusTransitionsAsync(options);
            await VerifyShipmentStatusConcurrencyAsync(options);
            await VerifyShipmentStatusRollbackAsync(options);
            await VerifyShipmentHistoryAsync(options);
            await VerifyShipmentHistoryRollbackAsync(options);
            await VerifyShipmentReadsAsync();
            Console.WriteLine("Shipment SQL verification passed");
        }
        finally { await DeleteVerificationDatabase(options); }
    }

    private sealed record ShipmentFixture(Guid OrderId, Guid PaymentId, int ProductId, OutboxMessage PaidEvent);

    private static async Task<ShipmentFixture> CreatePaidShipmentFixtureAsync(DbContextOptions<ShopDbContext> options)
    {
        var customer = $"shipment-{Guid.NewGuid():N}";
        int productId;
        Guid orderId, paymentId;
        await using (var db = new ShopDbContext(options))
        {
            var product = new Product { Name = "Shipment fixture", PriceCents = 5000, Available = 2 };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            productId = product.Id;
        }
        await using (var db = new ShopDbContext(options))
        {
            var result = await new OrderService(db).Create(customer, "checkout", new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
            Check(result.Error is null, "Shipment fixture order created");
            orderId = result.Order!.Id;
        }
        await using (var db = new ShopDbContext(options))
        {
            var result = await new PaymentService(db).Create(customer, orderId, "payment");
            Check(result.Error is null, "Shipment fixture payment created");
            paymentId = result.Payment!.Id;
        }
        await using (var db = new ShopDbContext(options))
            Check((await new PaymentService(db).SimulateSuccess(paymentId)).Error is null, "Shipment fixture payment succeeded");
        await using var read = new ShopDbContext(options);
        return new(orderId, paymentId, productId, await read.Outbox.AsNoTracking().SingleAsync(m => m.OrderId == orderId && m.Type == "OrderPaid"));
    }

    private static async Task VerifyShipmentSchemaAsync(DbContextOptions<ShopDbContext> options)
    {
        var fixture = await CreatePaidShipmentFixtureAsync(options);
        await using (var db = new ShopDbContext(options))
        {
            db.Shipments.Add(new Shipment { OrderId = fixture.OrderId });
            await db.SaveChangesAsync();
            Check(await db.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1, "Shipment persists for paid order");
        }
        async Task<bool> RejectShipment(Guid orderId)
        {
            await using var db = new ShopDbContext(options);
            db.Shipments.Add(new Shipment { OrderId = orderId });
            try { await db.SaveChangesAsync(); return false; }
            catch (DbUpdateException) { return true; }
        }
        Check(await RejectShipment(fixture.OrderId), "Database enforces one shipment per order");
        Check(await RejectShipment(Guid.NewGuid()), "Shipment requires existing order");
        // Remove other dependent rows so this rejection specifically exercises Shipments.
        await using (var db = new ShopDbContext(options))
        {
            await db.Outbox.Where(m => m.OrderId == fixture.OrderId).ExecuteDeleteAsync();
            await db.Payments.Where(p => p.OrderId == fixture.OrderId).ExecuteDeleteAsync();
            await db.Set<OrderItem>().Where(i => i.OrderId == fixture.OrderId).ExecuteDeleteAsync();
            var rejected = false;
            try { await db.Orders.Where(o => o.Id == fixture.OrderId).ExecuteDeleteAsync(); }
            catch (Microsoft.Data.SqlClient.SqlException) { rejected = true; }
            Check(rejected, "Shipment foreign key protects its order");
        }
    }

    private static async Task<bool> ConsumeShipmentEventAsync(DbContextOptions<ShopDbContext> options,
        OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = new ShopDbContext(options);
        return await new EventConsumer(db).ConsumeAsync(message, cancellationToken);
    }

    private static OutboxMessage CopyPaidEvent(ShipmentFixture fixture) => new()
    {
        OrderId = fixture.OrderId, Type = "OrderPaid", Payload = fixture.PaidEvent.Payload
    };

    private static async Task VerifyShipmentDeliveryAsync(DbContextOptions<ShopDbContext> options)
    {
        var fixture = await CreatePaidShipmentFixtureAsync(options);
        Check(await ConsumeShipmentEventAsync(options, fixture.PaidEvent), "First paid delivery processes");
        await using (var db = new ShopDbContext(options))
        {
            Check(await db.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1, "OrderPaid creates exactly one shipment");
            Check((await db.Shipments.SingleAsync(s => s.OrderId == fixture.OrderId)).Status == "Pending", "Shipment starts Pending");
            Check((await db.Orders.FindAsync(fixture.OrderId))!.Status == "Paid" &&
                (await db.Payments.FindAsync(fixture.PaymentId))!.Status == "Succeeded" &&
                (await db.Products.FindAsync(fixture.ProductId))!.Available == 1, "Shipment preserves payment/order/stock");
        }
        Check(!await ConsumeShipmentEventAsync(options, fixture.PaidEvent), "Same paid ID is ignored");
        var another = CopyPaidEvent(fixture);
        Check(await ConsumeShipmentEventAsync(options, another), "New paid ID is recorded");
        await using (var db = new ShopDbContext(options))
        {
            Check(await db.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1 &&
                await db.ProcessedMessages.CountAsync(m => m.MessageId == fixture.PaidEvent.Id || m.MessageId == another.Id) == 2,
                "Different paid IDs retain one shipment and both markers");
        }
        var historical = await CreatePaidShipmentFixtureAsync(options);
        await using (var db = new ShopDbContext(options))
        {
            db.ProcessedMessages.Add(new ProcessedMessage { MessageId = historical.PaidEvent.Id });
            await db.SaveChangesAsync();
        }
        historical.PaidEvent.Payload = "invalid historical payload";
        Check(!await ConsumeShipmentEventAsync(options, historical.PaidEvent), "Historical marker wins before payload decoding");
        await using var read = new ShopDbContext(options);
        Check(!await read.Shipments.AnyAsync(s => s.OrderId == historical.OrderId), "Historical events are not backfilled");
    }

    private static async Task VerifyShipmentConcurrentDeliveryAsync(DbContextOptions<ShopDbContext> options)
    {
        foreach (var sameId in new[] { true, false })
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var fixture = await CreatePaidShipmentFixtureAsync(options);
            var second = sameId ? fixture.PaidEvent : CopyPaidEvent(fixture);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Deliver(OutboxMessage message) { await start.Task; return await ConsumeShipmentEventAsync(options, message); }
            var firstTask = Deliver(fixture.PaidEvent);
            var secondTask = Deliver(second);
            start.SetResult();
            var results = await Task.WhenAll(firstTask, secondTask);
            await using var db = new ShopDbContext(options);
            Check(await db.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1 &&
                await db.ProcessedMessages.CountAsync(m => m.MessageId == fixture.PaidEvent.Id || m.MessageId == second.Id) == (sameId ? 1 : 2) &&
                results.Count(r => r) == (sameId ? 1 : 2), $"Concurrent paid delivery sameId={sameId}, attempt={attempt + 1}");
        }
    }

    private static async Task VerifyShipmentValidationAsync(DbContextOptions<ShopDbContext> options)
    {
        foreach (var scenario in new[] { "invalid-json", "null", "empty-event", "missing-envelope", "empty-order", "empty-payment", "mismatch", "missing-order", "missing-payment", "wrong-order", "pending-order", "cancelled-order", "pending-payment", "failed-payment" })
        {
            var fixture = await CreatePaidShipmentFixtureAsync(options);
            var message = CopyPaidEvent(fixture);
            switch (scenario)
            {
                case "invalid-json": message.Payload = "{"; break;
                case "null": message.Payload = "null"; break;
                case "empty-event": message.Id = Guid.Empty; break;
                case "missing-envelope": message.OrderId = null; break;
                case "empty-order": message.Payload = JsonSerializer.Serialize(new { OrderId = Guid.Empty, PaymentId = fixture.PaymentId }); break;
                case "empty-payment": message.Payload = JsonSerializer.Serialize(new { OrderId = fixture.OrderId }); break;
                case "mismatch": message.OrderId = Guid.NewGuid(); break;
                case "missing-order": message.OrderId = Guid.NewGuid(); message.Payload = JsonSerializer.Serialize(new { OrderId = message.OrderId, PaymentId = fixture.PaymentId }); break;
                case "missing-payment": message.Payload = JsonSerializer.Serialize(new { OrderId = fixture.OrderId, PaymentId = Guid.NewGuid() }); break;
                case "wrong-order":
                    var other = await CreatePaidShipmentFixtureAsync(options);
                    message.Payload = JsonSerializer.Serialize(new { OrderId = fixture.OrderId, PaymentId = other.PaymentId }); break;
                default:
                    await using (var db = new ShopDbContext(options))
                    {
                        if (scenario.EndsWith("order")) (await db.Orders.FindAsync(fixture.OrderId))!.Status = scenario == "pending-order" ? "PendingPayment" : "Cancelled";
                        else (await db.Payments.FindAsync(fixture.PaymentId))!.Status = scenario == "pending-payment" ? "Pending" : "Failed";
                        await db.SaveChangesAsync();
                    }
                    break;
            }
            var rejected = false;
            try { await ConsumeShipmentEventAsync(options, message); }
            catch (JsonException) { rejected = true; }
            await using var checkDb = new ShopDbContext(options);
            Check(rejected && !await checkDb.Shipments.AnyAsync(s => s.OrderId == fixture.OrderId) &&
                !await checkDb.ProcessedMessages.AnyAsync(m => m.MessageId == message.Id), $"Invalid paid event rejected without effects: {scenario}");
        }
        var cancelled = await CreatePaidShipmentFixtureAsync(options);
        var cancellationRejected = false;
        try { await ConsumeShipmentEventAsync(options, cancelled.PaidEvent, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancellationRejected = true; }
        await using var read = new ShopDbContext(options);
        Check(cancellationRejected && !await read.Shipments.AnyAsync(s => s.OrderId == cancelled.OrderId) &&
            !await read.ProcessedMessages.AnyAsync(m => m.MessageId == cancelled.PaidEvent.Id), "Cancelled delivery leaves no effects");
    }

    private static async Task VerifyShipmentRollbackAsync(DbContextOptions<ShopDbContext> options)
    {
        var fixture = await CreatePaidShipmentFixtureAsync(options);
        await using (var db = new ShopDbContext(options))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ProcessedMessages] WITH NOCHECK ADD CONSTRAINT [CK_ShipmentMarkerFailure] CHECK (1 = 0)");
        try
        {
            var rejected = false;
            try { await ConsumeShipmentEventAsync(options, fixture.PaidEvent); }
            catch (DbUpdateException) { rejected = true; }
            await using var read = new ShopDbContext(options);
            Check(rejected && !await read.Shipments.AnyAsync(s => s.OrderId == fixture.OrderId) &&
                !await read.ProcessedMessages.AnyAsync(m => m.MessageId == fixture.PaidEvent.Id), "Marker failure rolls back shipment and marker");
        }
        finally
        {
            await using var db = new ShopDbContext(options);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ProcessedMessages] DROP CONSTRAINT [CK_ShipmentMarkerFailure]");
        }
        Check(await ConsumeShipmentEventAsync(options, fixture.PaidEvent), "Failed shipment transaction can retry");
        await using var checkDb = new ShopDbContext(options);
        Check(await checkDb.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1 &&
            await checkDb.ProcessedMessages.CountAsync(m => m.MessageId == fixture.PaidEvent.Id) == 1, "Retry commits exactly one shipment and marker");
    }
}
