using Ecommerce.Features.Shipments.Models;
using Ecommerce.Features.Shipments.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    private static async Task VerifyShipmentHistoryAsync(DbContextOptions<ShopDbContext> options)
    {
        var fixture=await CreatePaidShipmentFixtureAsync(options);
        await ConsumeShipmentEventAsync(options,fixture.PaidEvent);
        Guid id;
        await using(var db=new ShopDbContext(options)) id=await db.Shipments.Where(s=>s.OrderId==fixture.OrderId).Select(s=>s.Id).SingleAsync();
        await using(var db=new ShopDbContext(options)) await new ShipmentService(db).UpdateStatusAsync(id,new("Shipped","HISTORY"),"actor-A");
        await using(var db=new ShopDbContext(options)) await new ShipmentService(db).UpdateStatusAsync(id,new("Delivered"),"actor-B");
        await ConsumeShipmentEventAsync(options,CopyPaidEvent(fixture));
        await using(var db=new ShopDbContext(options)) await new ShipmentService(db).UpdateStatusAsync(id,new("Delivered"),"actor-B");
        await using var read=new ShopDbContext(options);
        var rows=await read.ShipmentHistory.Where(h=>h.ShipmentId==id).OrderBy(h=>h.Id).ToListAsync();
        Check(rows.Select(h=>h.ToStatus).SequenceEqual(new[]{"Pending","Shipped","Delivered"}),"Committed shipment history contains exactly forward states");
        Check(rows[0].ActorId is null && rows[1].ActorId=="actor-A" && rows[2].ActorId=="actor-B" && rows[1].FromStatus=="Pending" && rows[2].FromStatus=="Shipped","History records system and real authenticated actors");
        var shipment=await read.Shipments.SingleAsync(s=>s.Id==id);
        Check(rows[1].OccurredAt==shipment.ShippedAt && rows[2].OccurredAt==shipment.DeliveredAt,"History shares transition timestamps");
        var result=await new ShipmentQueryService(read).GetHistoryAsync(id,CancellationToken.None);
        Check(result!.History.Count==3 && await new ShipmentQueryService(read).GetHistoryAsync(Guid.NewGuid(),CancellationToken.None) is null,"Admin history checks existence");
    }
    private static async Task VerifyShipmentHistoryRollbackAsync(DbContextOptions<ShopDbContext> options)
    {
        var existing=await CreatePaidShipmentFixtureAsync(options);
        await ConsumeShipmentEventAsync(options,existing.PaidEvent);
        Guid id;
        await using(var db=new ShopDbContext(options)) id=await db.Shipments.Where(s=>s.OrderId==existing.OrderId).Select(s=>s.Id).SingleAsync();
        var fresh=await CreatePaidShipmentFixtureAsync(options);
        await using(var db=new ShopDbContext(options)) await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ShipmentHistory] WITH NOCHECK ADD CONSTRAINT [CK_HistoryVerificationFailure] CHECK (1=0)");
        try
        {
            bool createFailed=false,updateFailed=false;
            try { await ConsumeShipmentEventAsync(options,fresh.PaidEvent); } catch(DbUpdateException) {createFailed=true;}
            try { await using var db=new ShopDbContext(options); await new ShipmentService(db).UpdateStatusAsync(id,new("Shipped","HISTORY"),"actor-A"); } catch(DbUpdateException) {updateFailed=true;}
            await using var read=new ShopDbContext(options);
            var row=await read.Shipments.SingleAsync(s=>s.Id==id);
            Check(createFailed && !await read.Shipments.AnyAsync(s=>s.OrderId==fresh.OrderId) && !await read.ProcessedMessages.AnyAsync(m=>m.MessageId==fresh.PaidEvent.Id),"History failure rolls back initial shipment and marker");
            Check(updateFailed && row.Status=="Pending" && row.TrackingNumber is null && row.ShippedAt is null && await read.ShipmentHistory.CountAsync(h=>h.ShipmentId==id)==1,"History failure rolls back shipping details and new audit entry");
        }
        finally { await using var db=new ShopDbContext(options); await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ShipmentHistory] DROP CONSTRAINT [CK_HistoryVerificationFailure]"); }
        Check(await ConsumeShipmentEventAsync(options,fresh.PaidEvent),"Shipment creation retries after audit recovery");
        await using(var db=new ShopDbContext(options)) Check((await new ShipmentService(db).UpdateStatusAsync(id,new("Shipped","HISTORY"),"actor-A")).Error is null,"Shipping retries after audit recovery");
    }
}
