using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Features.Shipments.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    private static async Task VerifyShipmentReadsAsync()
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(VerificationConnection($"Verify_fulfillment_reads_{Guid.NewGuid():N}")).Options;
        try
        {
            await using (var db = new ShopDbContext(options)) await db.Database.MigrateAsync();
            await VerifyShipmentListsAsync(options);
            await VerifyCustomerTrackingAsync(options);
        }
        finally { await DeleteVerificationDatabase(options); }
    }
    private static async Task VerifyShipmentListsAsync(DbContextOptions<ShopDbContext> options)
    {
        var rows = new[] {
            new Shipment { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Status="Pending", CreatedAt=new DateTime(2026,1,1) },
            new Shipment { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Status="Shipped", CreatedAt=new DateTime(2026,1,2) },
            new Shipment { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Status="Delivered", CreatedAt=new DateTime(2026,1,2) } };
        await using (var db = new ShopDbContext(options))
        {
            foreach (var row in rows)
            {
                var order = new Order { CustomerId="list-fixture", IdempotencyKey=row.Id.ToString(), Status="Paid" };
                db.Orders.Add(order); row.OrderId=order.Id; db.Shipments.Add(row);
            }
            await db.SaveChangesAsync();
        }
        await using var read = new ShopDbContext(options);
        var service = new ShipmentQueryService(read);
        var all = await service.ListAsync(null,null,null,CancellationToken.None);
        Check(all.Page==1 && all.PageSize==20 && all.Shipments.Select(s=>s.Id).SequenceEqual(rows.Reverse().Select(s=>s.Id)), "Shipment list stable newest-first ordering including tied dates");
        foreach (var status in new[] {"Pending","Shipped","Delivered"})
            Check((await service.ListAsync(status,1,20,CancellationToken.None)).Shipments.Single().Status==status,"Shipment status filter matches only requested state");
        var p1=await service.ListAsync(null,1,1,CancellationToken.None);
        var p2=await service.ListAsync(null,2,1,CancellationToken.None);
        Check(p1.Shipments.Single().Id!=p2.Shipments.Single().Id,"Shipment pages are disjoint");
        var normalized=await service.ListAsync(null,0,0,CancellationToken.None);
        Check(normalized.Page==1 && normalized.PageSize==1,"Shipment paging minima normalized");
        Check((await service.ListAsync(null,1,1000,CancellationToken.None)).PageSize==50,"Shipment page cap");
        Check((await service.ListAsync(null,int.MaxValue,50,CancellationToken.None)).Shipments.Count==0,"Shipment offset cannot overflow");
    }
    private static async Task VerifyCustomerTrackingAsync(DbContextOptions<ShopDbContext> options)
    {
        var unpaid=new Order {CustomerId="tracking-owner",IdempotencyKey=Guid.NewGuid().ToString()};
        await using(var db=new ShopDbContext(options)) {db.Orders.Add(unpaid);await db.SaveChangesAsync();}
        await using(var db=new ShopDbContext(options))
        {
            var query=new ShipmentQueryService(db);
            var empty=await query.GetTrackingAsync("tracking-owner",unpaid.Id,CancellationToken.None);
            Check(empty is not null && empty.OrderStatus=="PendingPayment" && empty.Shipment is null && empty.History.Count==0,"Tracking shows owned order before fulfillment");
            Check(await query.GetTrackingAsync("someone-else",unpaid.Id,CancellationToken.None) is null && await query.GetTrackingAsync("tracking-owner",Guid.NewGuid(),CancellationToken.None) is null,"Tracking hides missing and other-owned orders");
        }
        var fixture=await CreatePaidShipmentFixtureAsync(options);
        await ConsumeShipmentEventAsync(options,fixture.PaidEvent);
        string owner;Guid shipmentId;
        await using(var db=new ShopDbContext(options))
        {
            owner=(await db.Orders.FindAsync(fixture.OrderId))!.CustomerId;
            shipmentId=await db.Shipments.Where(s=>s.OrderId==fixture.OrderId).Select(s=>s.Id).SingleAsync();
        }
        var gate=new TrackingReadGate();
        var gatedOptions=new DbContextOptionsBuilder<ShopDbContext>(options).AddInterceptors(gate).Options;
        var tracking=Task.Run(async()=> {await using var db=new ShopDbContext(gatedOptions);return await new ShipmentQueryService(db).GetTrackingAsync(owner,fixture.OrderId,CancellationToken.None);});
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var writer=Task.Run(async()=>
        {
            try {await using var db=new ShopDbContext(options);return await new ShipmentService(db).UpdateStatusAsync(shipmentId,new("Shipped","TRACK-RACE"),"tracking-admin");}
            finally {gate.WriterDone.TrySetResult();}
        });
        try
        {
            var response=await tracking.WaitAsync(TimeSpan.FromSeconds(10));
            Check(response!.History.Last().ToStatus==response.Shipment!.Status,"Tracking state and history are consistent during concurrent shipping");
            Check((await writer.WaitAsync(TimeSpan.FromSeconds(10))).Error is null,"Tracking transaction releases locks to allow writer");
        }
        finally {gate.Release.TrySetResult();await Task.WhenAll(tracking,writer).WaitAsync(TimeSpan.FromSeconds(10));}
        await using(var db=new ShopDbContext(options))
        {
            await new ShipmentService(db).UpdateStatusAsync(shipmentId,new("Delivered"),"tracking-admin");
            var result=await new ShipmentQueryService(db).GetTrackingAsync(owner,fixture.OrderId,CancellationToken.None);
            Check(result!.Shipment!.Status=="Delivered" && result.History.Select(h=>h.ToStatus).SequenceEqual(new[]{"Pending","Shipped","Delivered"}),"Customer sees current delivered timeline");
            var json=System.Text.Json.JsonSerializer.Serialize(result,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Check(!json.Contains("actorId") && !json.Contains("tracking-admin"),"Customer tracking excludes administrator identity");
        }
    }
    private sealed class TrackingReadGate : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriterDone {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,System.Data.Common.DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("FROM [Shipments]") && !Entered.Task.IsCompleted)
            {
                Entered.TrySetResult();
                await Task.WhenAny(WriterDone.Task,Release.Task,Task.Delay(500,ct));
            }
            return result;
        }
    }

}
