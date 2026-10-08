using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Shipments.Contracts;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Features.Shipments.Services;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static async Task<Guid> CreateShipmentStatusFixtureAsync(DbContextOptions<ShopDbContext> options)
    {
        var fixture = await CreatePaidShipmentFixtureAsync(options);
        await ConsumeShipmentEventAsync(options, fixture.PaidEvent);
        await using var db = new ShopDbContext(options);
        return await db.Shipments.Where(s => s.OrderId == fixture.OrderId).Select(s => s.Id).SingleAsync();
    }

    private static async Task<ShipmentUpdateResult> UpdateShipmentAsync(DbContextOptions<ShopDbContext> options,
        Guid id, string? status, string? tracking = null, CancellationToken cancellationToken = default)
    {
        await using var db = new ShopDbContext(options);
        return await new ShipmentService(db).UpdateStatusAsync(id, new UpdateShipmentStatusRequest(status, tracking), "verification-admin", cancellationToken);
    }

    private static async Task VerifyShipmentStatusTransitionsAsync(DbContextOptions<ShopDbContext> options)
    {
        var id = await CreateShipmentStatusFixtureAsync(options);
        await using (var db = new ShopDbContext(options))
        {
            var pending = await db.Shipments.SingleAsync(s => s.Id == id);
            Check(pending.TrackingNumber is null && pending.ShippedAt is null && pending.DeliveredAt is null,
                "Pending shipment has no tracking or transition timestamps");
        }
        Check((await UpdateShipmentAsync(options, Guid.NewGuid(), "Shipped", "TRACK-001")).Error == ShipmentUpdateError.NotFound,
            "Unknown shipment returns not found");
        foreach (var request in new[] { new UpdateShipmentStatusRequest(null),
            new UpdateShipmentStatusRequest("Lost"), new UpdateShipmentStatusRequest("Shipped"), new UpdateShipmentStatusRequest("Shipped", "   "),
            new UpdateShipmentStatusRequest("Shipped", new string('x', 101)), new UpdateShipmentStatusRequest("Shipped", "bad\ntracking") })
            Check((await UpdateShipmentAsync(options, id, request.Status, request.TrackingNumber)).Error == ShipmentUpdateError.InvalidRequest,
                "Invalid shipment request rejected");
        Check((await UpdateShipmentAsync(options, id, "Delivered")).Error == ShipmentUpdateError.Conflict,
            "Pending cannot skip directly to Delivered");
        var shipped = await UpdateShipmentAsync(options, id, "Shipped", "  TRACK-001  ");
        Check(shipped.Error is null && !shipped.Replayed && shipped.Shipment!.Status == "Shipped" &&
            shipped.Shipment.TrackingNumber == "TRACK-001" && shipped.Shipment.ShippedAt is not null && shipped.Shipment.DeliveredAt is null,
            "Pending becomes Shipped with normalized tracking and timestamp");
        var shippedAt = shipped.Shipment!.ShippedAt;
        Check((await UpdateShipmentAsync(options, id, "Pending")).Error == ShipmentUpdateError.Conflict,
            "Shipped cannot move backward to Pending");
        var replay = await UpdateShipmentAsync(options, id, "Shipped", "TRACK-001");
        Check(replay.Error is null && replay.Replayed && replay.Shipment!.ShippedAt == shippedAt,
            "Shipped replay preserves original timestamp");
        Check((await UpdateShipmentAsync(options, id, "Shipped", "TRACK-002")).Error == ShipmentUpdateError.Conflict,
            "Shipped replay cannot replace tracking");
        Check((await UpdateShipmentAsync(options, id, "Delivered", "TRACK-002")).Error == ShipmentUpdateError.Conflict,
            "Delivery cannot replace tracking");
        var delivered = await UpdateShipmentAsync(options, id, "Delivered");
        Check(delivered.Error is null && !delivered.Replayed && delivered.Shipment!.Status == "Delivered" &&
            delivered.Shipment.ShippedAt == shippedAt && delivered.Shipment.DeliveredAt >= shippedAt && delivered.Shipment.TrackingNumber == "TRACK-001",
            "Shipped becomes Delivered preserving shipment details");
        var deliveredAt = delivered.Shipment!.DeliveredAt;
        replay = await UpdateShipmentAsync(options, id, "Delivered", "TRACK-001");
        Check(replay.Error is null && replay.Replayed && replay.Shipment!.DeliveredAt == deliveredAt,
            "Delivered replay preserves original timestamp");
        Check((await UpdateShipmentAsync(options, id, "Pending")).Error == ShipmentUpdateError.Conflict,
            "Delivered cannot move backward to Pending");
        Check((await UpdateShipmentAsync(options, id, "Shipped", "TRACK-001")).Error == ShipmentUpdateError.Conflict,
            "Delivered cannot move backward to Shipped");
        await using var read = new ShopDbContext(options);
        var shipment = await read.Shipments.SingleAsync(s => s.Id == id);
        var order = await read.Orders.SingleAsync(o => o.Id == shipment.OrderId);
        var paid = await read.Outbox.AsNoTracking().SingleAsync(m => m.OrderId == order.Id && m.Type == "OrderPaid");
        var replayEvent = new OutboxMessage { OrderId = order.Id, Type = "OrderPaid", Payload = paid.Payload };
        Check(await ConsumeShipmentEventAsync(options, replayEvent), "Different paid event records its marker after delivery");
        await using (var refreshed = new ShopDbContext(options))
        {
            var deliveredRow = await refreshed.Shipments.SingleAsync(s => s.Id == id);
            Check(deliveredRow.Status == "Delivered" && deliveredRow.TrackingNumber == "TRACK-001" &&
                deliveredRow.ShippedAt == shippedAt && deliveredRow.DeliveredAt == deliveredAt,
                "Paid event redelivery cannot reset shipment progress");
        }
        Check(order.Status == "Paid" && await read.Payments.AnyAsync(p => p.OrderId == order.Id && p.Status == "Succeeded") &&
            (await read.Products.FindAsync(await read.Set<OrderItem>().Where(i => i.OrderId == order.Id).Select(i => i.ProductId).SingleAsync()))!.Available == 1,
            "Shipment transitions preserve payment, order and reserved inventory");
    }

    private static async Task VerifyShipmentStatusConcurrencyAsync(DbContextOptions<ShopDbContext> options)
    {
        foreach (var sameTracking in new[] { true, false })
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = await CreateShipmentStatusFixtureAsync(options);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<ShipmentUpdateResult> Ship(string tracking)
            {
                await start.Task;
                return await UpdateShipmentAsync(options, id, "Shipped", tracking);
            }
            var first = Ship("RACE-A");
            var second = Ship(sameTracking ? "RACE-A" : "RACE-B");
            start.SetResult();
            var results = await Task.WhenAll(first, second);
            await using var db = new ShopDbContext(options);
            var row = await db.Shipments.SingleAsync(s => s.Id == id);
            Check(results.Count(r => r.Error is null && !r.Replayed) == 1 &&
                (sameTracking ? results.Count(r => r.Replayed) == 1 : results.Count(r => r.Error == ShipmentUpdateError.Conflict) == 1) &&
                row.Status == "Shipped" && row.ShippedAt is not null && row.DeliveredAt is null &&
                (row.TrackingNumber == "RACE-A" || !sameTracking && row.TrackingNumber == "RACE-B"),
                $"Concurrent shipping has one update and safe replay/conflict sameTracking={sameTracking} attempt={attempt + 1}");
            var deliveryStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<ShipmentUpdateResult> Deliver() { await deliveryStart.Task; return await UpdateShipmentAsync(options, id, "Delivered"); }
            var d1 = Deliver(); var d2 = Deliver(); deliveryStart.SetResult();
            results = await Task.WhenAll(d1, d2);
            Check(results.All(r => r.Error is null) && results.Count(r => r.Replayed) == 1 &&
                results[0].Shipment!.DeliveredAt == results[1].Shipment!.DeliveredAt,
                "Concurrent delivery updates once and preserves timestamp");
            Check(await db.ShipmentHistory.CountAsync(h=>h.ShipmentId==id)==3,
                "Concurrent shipment transitions create exactly one history entry per state");
        }
    }

    private static async Task VerifyShipmentStatusRollbackAsync(DbContextOptions<ShopDbContext> options)
    {
        var id = await CreateShipmentStatusFixtureAsync(options);
        var cancelled = false;
        try { await UpdateShipmentAsync(options, id, "Shipped", "ROLLBACK", new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Cancelled status update is propagated");
        await using (var db = new ShopDbContext(options))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [Shipments] WITH NOCHECK ADD CONSTRAINT [CK_StatusVerificationFailure] CHECK ([Status] = N'Pending')");
        try
        {
            var failed = false;
            try { await UpdateShipmentAsync(options, id, "Shipped", "ROLLBACK"); }
            catch (DbUpdateException) { failed = true; }
            await using var read = new ShopDbContext(options);
            var row = await read.Shipments.SingleAsync(s => s.Id == id);
            Check(failed && row.Status == "Pending" && row.TrackingNumber is null && row.ShippedAt is null && row.DeliveredAt is null,
                "Failed status save rolls back status, tracking and timestamps");
        }
        finally
        {
            await using var db = new ShopDbContext(options);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [Shipments] DROP CONSTRAINT [CK_StatusVerificationFailure]");
        }
        Check((await UpdateShipmentAsync(options, id, "Shipped", "ROLLBACK")).Error is null,
            "Status update retries after database recovery");
    }
}
