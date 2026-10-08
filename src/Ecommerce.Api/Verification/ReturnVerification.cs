using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Features.Returns.Models;
using Ecommerce.Features.Returns.Services;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    public static async Task RunReturns()
    {
        var options=new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(VerificationConnection($"Verify_ecommerce_returns_{Guid.NewGuid():N}")).Options;
        try
        {
            await using(var db=new ShopDbContext(options)) await db.Database.MigrateAsync();
            var fixture=await CreatePaidShipmentFixtureAsync(options);
            string owner;
            await using(var db=new ShopDbContext(options)) owner=(await db.Orders.FindAsync(fixture.OrderId))!.CustomerId;
            async Task<ReturnResult> Create(string customer,string key,string reason)
            { await using var db=new ShopDbContext(options);return await new ReturnService(db,new RefundService(db)).CreateAsync(customer,fixture.OrderId,key,new(reason),default); }
            Check((await Create("other","key","damaged")).Error==ReturnError.NotFound,"Return ownership hides other orders");
            Check((await Create(owner,"","damaged")).Error==ReturnError.InvalidRequest,"Return requires idempotency key");
            Check((await Create(owner,"key"," ")).Error==ReturnError.InvalidRequest,"Return rejects blank reason");
            Check((await Create(owner,"key",new string('x',501))).Error==ReturnError.InvalidRequest,"Return reason maximum enforced");
            Check((await Create(owner,new string('k',101),"damaged")).Error==ReturnError.InvalidRequest,"Return key maximum enforced");
            Check((await Create(owner,"key","damaged")).Error==ReturnError.Conflict,"Return requires delivered shipment");
            await ConsumeShipmentEventAsync(options,fixture.PaidEvent);
            Guid shipmentId;
            await using(var db=new ShopDbContext(options)) shipmentId=await db.Shipments.Where(s=>s.OrderId==fixture.OrderId).Select(s=>s.Id).SingleAsync();
            await UpdateShipmentAsync(options,shipmentId,"Shipped","RETURN-TRACK");
            await UpdateShipmentAsync(options,shipmentId,"Delivered");
            var pair=await Task.WhenAll(Create(owner,"key"," damaged "),Create(owner,"key","damaged")).WaitAsync(TimeSpan.FromSeconds(10));
            Check(pair.All(r=>r.Error is null)&&pair.Select(r=>r.Return!.Id).Distinct().Count()==1&&pair.Count(r=>r.Replayed)==1,"Concurrent return replay creates one request");
            var id=pair[0].Return!.Id;
            Check((await Create(owner,"other-key","damaged")).Error==ReturnError.Conflict,"Different return key conflicts");
            Check((await Create(owner,"key","different")).Error==ReturnError.Conflict,"Return replay body conflict");
            await using(var db=new ShopDbContext(options))
            {
                var query=new ReturnQueryService(db);
                var result=await query.GetForOwnerAsync(owner,fixture.OrderId,default);
                Check(result is {Status:"Requested",OriginalAmountCents:5000,RefundedCents:0,ReservedRefundCents:0,RemainingRefundableCents:5000},"Return financial read reflects original payment");
                Check(await query.GetForOwnerAsync("other",fixture.OrderId,default) is null,"Return read enforces owner");
                Check(await db.ReturnRequests.CountAsync(r=>r.OrderId==fixture.OrderId)==1,"Database has one return per order");
                Check((await db.Products.FindAsync(fixture.ProductId))!.Available==1,"Return request does not restock");
            }
            async Task<ReturnResult> Update(string status)
            {await using var db=new ShopDbContext(options);return await new ReturnService(db,new RefundService(db)).UpdateStatusAsync(id,new(status),default);}
            async Task<RefundResult> Refund(long amount,string key)
            {await using var db=new ShopDbContext(options);return await new ReturnService(db,new RefundService(db)).CreateRefundAsync(id,amount,key,default);}
            Check((await Update("Received")).Error==ReturnError.Conflict,"Return cannot skip approval");
            Check((await Refund(5000,"early")).Error is not null,"Return refund requires receipt");
            var approved=await Update("Approved");Check(approved.Error is null&&approved.Return!.ApprovedAt is not null,"Return approved timestamp");
            Check((await Update("Approved")).Return!.ApprovedAt==approved.Return!.ApprovedAt,"Approval replay preserves timestamp");
            Check((await Update("Requested")).Error==ReturnError.Conflict,"Return cannot reset");
            Check((await Update("Received")).Error is null,"Approved return can be received");
            Check((await Update("Completed")).Error==ReturnError.Conflict,"Completion requires settled full refund");
            var first=await Refund(2000,"partial");Check(first.Error is null,"Received return uses existing partial refund flow");
            Check((await Refund(2000,"partial")).Replayed,"Return refund key replays");
            Check((await Refund(1,"parallel")).Error is not null,"Pending refund blocks another reservation");
            await using(var db=new ShopDbContext(options))
            {
                var current=await new ReturnQueryService(db).GetByIdAsync(id,default);
                Check(current is {ReservedRefundCents:2000,RefundedCents:0,RemainingRefundableCents:3000},"Pending refund updates live financial totals");
                await new RefundService(db).SimulateTimeout(first.Refund!.Id);
            }
            Check((await Update("Completed")).Error==ReturnError.Conflict,"Unknown refund prevents completion");
            await using(var db=new ShopDbContext(options)) await new RefundService(db).SimulateFailure(first.Refund!.Id);
            var full=await Refund(5000,"full");Check(full.Error is null,"Failed reservation releases remaining amount");
            await using(var db=new ShopDbContext(options)) await new RefundService(db).SimulateSuccess(full.Refund!.Id);
            var completions=await Task.WhenAll(Update("Completed"),Update("Completed")).WaitAsync(TimeSpan.FromSeconds(10));
            Check(completions.All(r=>r.Error is null)&&completions.Count(r=>r.Replayed)==1,"Concurrent completion writes once and replays");
            Check(completions[0].Return!.CompletedAt==completions[1].Return!.CompletedAt,"Completion timestamp is stable");
            var settledReplay=await Refund(5000,"full");
            Check(settledReplay.Error is null&&settledReplay.Replayed&&settledReplay.Refund!.Id==full.Refund!.Id,"Refund key replays after return completion");
            Check((await Refund(5000,"new-after-completion")).Error is not null,"Completed return cannot create another refund");
            Check((await Refund(4000,"full")).Error is not null,"Completed refund replay rejects changed amount");
            Check((await Create(owner,"key"," damaged ")).Replayed,"Original return key replays after full refund and completion");
            await using(var db=new ShopDbContext(options))
            {
                var result=await new ReturnQueryService(db).GetByIdAsync(id,default);
                Check(result is {Status:"Completed",RefundedCents:5000,ReservedRefundCents:0,RemainingRefundableCents:0},"Completed return reads settled financial totals");
                Check((await db.Products.FindAsync(fixture.ProductId))!.Available==1,"Return and refund never automatically restock");
                var page=await new ReturnQueryService(db).ListAsync("Completed",1,20,default);
                Check(page.Returns.Count==1&&page.Returns[0].Id==id,"Admin return list filters completion");
                Check((await new ReturnQueryService(db).ListAsync(null,int.MaxValue,50,default)).Returns.Count==0,"Admin return list safely handles page overflow");
            }
            await VerifyReturnRacesAndRollbackAsync(options);
            await VerifyReturnListOrderingAsync(options);
            await VerifyReturnFilteredListRaceAsync(options);
            Console.WriteLine("Return SQL verification passed");
        }
        finally {await DeleteVerificationDatabase(options);}
    }
    private static async Task VerifyReturnRacesAndRollbackAsync(DbContextOptions<ShopDbContext> options)
    {
        async Task<(ShipmentFixture Fixture,string Owner)> Delivered()
        {
            var fixture=await CreatePaidShipmentFixtureAsync(options);
            await using var db=new ShopDbContext(options);
            var owner=(await db.Orders.FindAsync(fixture.OrderId))!.CustomerId;
            db.Shipments.Add(new Shipment {OrderId=fixture.OrderId,Status="Delivered",TrackingNumber="HISTORIC",ShippedAt=DateTime.UtcNow,DeliveredAt=DateTime.UtcNow});
            await db.SaveChangesAsync();return(fixture,owner);
        }
        async Task<ReturnResult> Create(Guid order,string owner,string key="same",string reason="damaged")
        {await using var db=new ShopDbContext(options);return await new ReturnService(db,new RefundService(db)).CreateAsync(owner,order,key,new(reason),default);}
        async Task<ReturnResult> Update(Guid id,string status)
        {await using var db=new ShopDbContext(options);return await new ReturnService(db,new RefundService(db)).UpdateStatusAsync(id,new(status),default);}
        var rollback=await Delivered();
        await using(var db=new ShopDbContext(options)) await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ReturnRequests] WITH NOCHECK ADD CONSTRAINT [CK_ReturnVerify] CHECK (1=0)");
        try
        {
            var failed=false;try {await Create(rollback.Fixture.OrderId,rollback.Owner);}catch(DbUpdateException){failed=true;}
            await using var db=new ShopDbContext(options);
            Check(failed&&!await db.ReturnRequests.AnyAsync(r=>r.OrderId==rollback.Fixture.OrderId),"Forced return insert failure leaves no request");
        }
        finally {await using var db=new ShopDbContext(options);await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ReturnRequests] DROP CONSTRAINT [CK_ReturnVerify]");}
        var restored=await Create(rollback.Fixture.OrderId,rollback.Owner,new string('k',100),new string('r',500));
        Check(restored.Error is null,"Historical delivery without history accepts return and maximum lengths");
        await using(var db=new ShopDbContext(options)) await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ReturnRequests] WITH NOCHECK ADD CONSTRAINT [CK_ReturnVerify] CHECK (Status <> N'Approved')");
        try
        {
            var failed=false;try {await Update(restored.Return!.Id,"Approved");}catch(DbUpdateException){failed=true;}
            await using var db=new ShopDbContext(options);var value=await db.ReturnRequests.FindAsync(restored.Return!.Id);
            Check(failed&&value is {Status:"Requested",ApprovedAt:null},"Forced return update failure rolls back status and timestamp");
        }
        finally {await using var db=new ShopDbContext(options);await db.Database.ExecuteSqlRawAsync("ALTER TABLE [ReturnRequests] DROP CONSTRAINT [CK_ReturnVerify]");}
        Check((await Update(restored.Return!.Id,"Approved")).Error is null,"Return update retries after rollback");
        async Task<bool> Reject(ReturnRequest value)
        {await using var db=new ShopDbContext(options);db.ReturnRequests.Add(value);try{await db.SaveChangesAsync();return false;}catch(DbUpdateException){return true;}}
        Check(await Reject(new ReturnRequest{OrderId=rollback.Fixture.OrderId,PaymentId=rollback.Fixture.PaymentId}),"Database rejects duplicate return order");
        Check(await Reject(new ReturnRequest{OrderId=Guid.NewGuid(),PaymentId=rollback.Fixture.PaymentId}),"Return requires existing order");
        var missingPayment=await Delivered();
        Check(await Reject(new ReturnRequest{OrderId=missingPayment.Fixture.OrderId,PaymentId=Guid.NewGuid()}),"Return requires existing payment");
        var alreadyRefunded=await Delivered();
        await using(var db=new ShopDbContext(options))
        {var refund=await new RefundService(db).Create(alreadyRefunded.Fixture.PaymentId,5000,"full");await new RefundService(db).SimulateSuccess(refund.Refund!.Id);}
        Check((await Create(alreadyRefunded.Fixture.OrderId,alreadyRefunded.Owner)).Error==ReturnError.Conflict,"Fully refunded order cannot create new return");
        var partial=await Delivered();var partialReturn=await Create(partial.Fixture.OrderId,partial.Owner);
        await Update(partialReturn.Return!.Id,"Approved");await Update(partialReturn.Return.Id,"Received");
        await using(var db=new ShopDbContext(options))
        {
            var refund=await new ReturnService(db,new RefundService(db)).CreateRefundAsync(partialReturn.Return.Id,2000,"installment-1",default);
            await new RefundService(db).SimulateSuccess(refund.Refund!.Id);
        }
        Check((await Update(partialReturn.Return.Id,"Completed")).Error==ReturnError.Conflict,"Partial successful refund cannot complete whole-order return");
        await using(var db=new ShopDbContext(options))
        {
            var refund=await new ReturnService(db,new RefundService(db)).CreateRefundAsync(partialReturn.Return.Id,3000,"installment-2",default);
            await new RefundService(db).SimulateSuccess(refund.Refund!.Id);
        }
        Check((await Update(partialReturn.Return.Id,"Completed")).Error is null,"Settled installments complete after refunding full original amount");
        for(var iteration=0;iteration<5;iteration++)
        {
            var item=await Delivered();
            var creations=await Task.WhenAll(Create(item.Fixture.OrderId,item.Owner,"key-a"),Create(item.Fixture.OrderId,item.Owner,"key-b")).WaitAsync(TimeSpan.FromSeconds(10));
            Check(creations.Count(r=>r.Error is null)==1&&creations.Count(r=>r.Error==ReturnError.Conflict)==1,"Different-key concurrent returns select one winner");
            var id=creations.Single(r=>r.Error is null).Return!.Id;
            await Update(id,"Approved");await Update(id,"Received");
            async Task<RefundResult> OwnerRefund()
            {await using var db=new ShopDbContext(options);return await new RefundService(db).Create(item.Fixture.PaymentId,5000,"owner");}
            async Task<RefundResult> AdminRefund()
            {await using var db=new ShopDbContext(options);return await new ReturnService(db,new RefundService(db)).CreateRefundAsync(id,5000,"admin",default);}
            var refunds=await Task.WhenAll(OwnerRefund(),AdminRefund()).WaitAsync(TimeSpan.FromSeconds(10));
            Check(refunds.Count(r=>r.Error is null)==1,"Owner and return admin share refund reservation lock");
            var winner=refunds.Single(r=>r.Error is null).Refund!;
            var completion=Update(id,"Completed");
            var settlement=Task.Run(async()=>{await using var db=new ShopDbContext(options);return await new RefundService(db).SimulateSuccess(winner.Id);});
            await Task.WhenAll(completion,settlement).WaitAsync(TimeSpan.FromSeconds(10));
            Check(settlement.Result.Error is null,"Concurrent refund settlement succeeds without deadlock");
            Check((await Update(id,"Completed")).Error is null,"Completion succeeds after concurrent settlement");
            var repeats=await Task.WhenAll(Update(id,"Completed"),Update(id,"Completed")).WaitAsync(TimeSpan.FromSeconds(10));
            Check(repeats.All(r=>r.Replayed)&&repeats[0].Return!.CompletedAt==repeats[1].Return!.CompletedAt,"Repeated concurrent completion preserves settled timestamp");
            await Task.WhenAll(Update(id,"Completed"),AdminRefund()).WaitAsync(TimeSpan.FromSeconds(10));
            await using var read=new ShopDbContext(options);
            var all=await read.Refunds.Where(r=>r.PaymentId==item.Fixture.PaymentId).ToListAsync();
            Check(all.Where(r=>r.Status is "Succeeded" or "Pending" or "Unknown").Sum(r=>r.AmountCents)<=5000&&all.All(r=>r.Status is not ("Pending" or "Unknown")),"Completed return has full settlement without over-refund");
            Check((await read.Products.FindAsync(item.Fixture.ProductId))!.Available==1,"Concurrent return flow preserves stock");
        }
    }
    private static async Task VerifyReturnListOrderingAsync(DbContextOptions<ShopDbContext> options)
    {
        await using var db=new ShopDbContext(options);
        var created=new List<Guid>();
        for(var i=1;i<=3;i++)
        {
            var order=new Order{CustomerId="list-owner",IdempotencyKey=Guid.NewGuid().ToString(),Status="Paid"};
            var payment=new Payment{OrderId=order.Id,Status="Succeeded",AmountCents=5000};
            var value=new ReturnRequest{Id=Guid.Parse($"ffffffff-ffff-ffff-ffff-{i:000000000000}"),OrderId=order.Id,PaymentId=payment.Id,Reason="list",Key="same-key",Status="Requested",CreatedAt=new DateTime(2090,1,i==1?1:2)};
            db.AddRange(order,payment,value);created.Add(value.Id);
        }
        await db.SaveChangesAsync();var query=new ReturnQueryService(db);
        var first=await query.ListAsync("Requested",1,2,default);var second=await query.ListAsync("Requested",2,2,default);
        Check(first.Returns.Select(r=>r.Id).SequenceEqual(created.AsEnumerable().Reverse().Take(2))&&second.Returns[0].Id==created[0],"Return list newest-first tie ordering and disjoint pages");
        Check(first.Returns.All(r=>r.Status=="Requested"),"Return list exact status filter");
        Check((await query.ListAsync(null,0,0,default)) is {Page:1,PageSize:1},"Return list normalizes minima");
        Check((await query.ListAsync(null,1,100,default)).PageSize==50,"Return list caps page size");
    }
}
