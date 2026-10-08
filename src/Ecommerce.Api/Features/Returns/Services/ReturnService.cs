using Ecommerce.Features.Refunds.Services;
using Ecommerce.Features.Returns.Contracts;
using Ecommerce.Features.Returns.Models;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Observability;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Features.Returns.Services;
public enum ReturnError {InvalidRequest,NotFound,Conflict}
public record ReturnResult(ReturnRequest? Return,ReturnError? Error=null,string? Detail=null,bool Replayed=false);
public class ReturnService(ShopDbContext db,RefundService refunds)
{
    private async Task<ReturnResult> CreateAsyncMeasuredCore(string customerId,Guid orderId,string? key,CreateReturnRequest request,CancellationToken ct)
    {
        var reason=request.Reason?.Trim();
        if(string.IsNullOrWhiteSpace(key)||key.Length>100||string.IsNullOrEmpty(reason)||reason.Length>500)
            return new(null,ReturnError.InvalidRequest,"A key of 1–100 characters and a reason of 1–500 characters are required.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var order=await db.Orders.FromSqlInterpolated($"SELECT * FROM [Orders] WITH (UPDLOCK,HOLDLOCK) WHERE Id = {orderId}").SingleOrDefaultAsync(ct);
        if(order is null||order.CustomerId!=customerId) return new(null,ReturnError.NotFound);
        var payment=await db.Payments.FromSqlInterpolated($"SELECT * FROM [Payments] WITH (UPDLOCK,HOLDLOCK) WHERE OrderId = {orderId} AND Status = N'Succeeded'").SingleOrDefaultAsync(ct);
        var existing=await db.ReturnRequests.FromSqlInterpolated($"SELECT * FROM [ReturnRequests] WITH (UPDLOCK,HOLDLOCK) WHERE OrderId = {orderId}").SingleOrDefaultAsync(ct);
        if(existing is not null)
            return existing.Key==key&&existing.Reason==reason?new(existing,Replayed:true):new(null,ReturnError.Conflict,"This order already has a return request with a different key or reason.");
        if(order.Status!="Paid"||payment is null||!await db.Shipments.AnyAsync(s=>s.OrderId==orderId&&s.Status=="Delivered",ct))
            return new(null,ReturnError.Conflict,"Only a paid, delivered order can be returned.");
        var refunded=await db.Refunds.Where(r=>r.PaymentId==payment.Id&&r.Status=="Succeeded").SumAsync(r=>(long?)r.AmountCents,ct)??0;
        if(refunded>=payment.AmountCents) return new(null,ReturnError.Conflict,"This order has already been fully refunded.");
        var value=new ReturnRequest {OrderId=orderId,PaymentId=payment.Id,Key=key,Reason=reason};
        db.ReturnRequests.Add(value);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        return new(value);
    }
    private async Task<ReturnResult> UpdateStatusAsyncMeasuredCore(Guid id,UpdateReturnStatusRequest request,CancellationToken ct)
    {
        if(request.Status is not ("Requested" or "Approved" or "Received" or "Completed"))
            return new(null,ReturnError.InvalidRequest,"Unknown return status.");
        var identity=await db.ReturnRequests.AsNoTracking().Where(r=>r.Id==id).Select(r=>new {r.OrderId,r.PaymentId}).SingleOrDefaultAsync(ct);
        if(identity is null) return new(null,ReturnError.NotFound);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var order=await db.Orders.FromSqlInterpolated($"SELECT * FROM [Orders] WITH (UPDLOCK,HOLDLOCK) WHERE Id = {identity.OrderId}").SingleOrDefaultAsync(ct);
        var payment=await db.Payments.FromSqlInterpolated($"SELECT * FROM [Payments] WITH (UPDLOCK,HOLDLOCK) WHERE Id = {identity.PaymentId}").SingleOrDefaultAsync(ct);
        var value=await db.ReturnRequests.FromSqlInterpolated($"SELECT * FROM [ReturnRequests] WITH (UPDLOCK,HOLDLOCK) WHERE Id = {id}").SingleOrDefaultAsync(ct);
        if(value is null||order is null||payment is null||payment.OrderId!=value.OrderId||value.OrderId!=order.Id||value.PaymentId!=payment.Id)
            return new(null,ReturnError.NotFound);
        if(request.Status=="Requested") return new(null,ReturnError.Conflict,"A return cannot be reset.");
        if(value.Status==request.Status) return new(value,Replayed:true);
        var expected=value.Status switch {"Requested"=>"Approved","Approved"=>"Received","Received"=>"Completed",_=>null};
        if(request.Status!=expected) return new(null,ReturnError.Conflict,"Return states must progress Requested, Approved, Received, Completed.");
        if(request.Status=="Completed")
        {
            var amounts=await db.Refunds.AsNoTracking().Where(r=>r.PaymentId==payment.Id).ToListAsync(ct);
            if(amounts.Any(r=>r.Status is "Pending" or "Unknown")||amounts.Where(r=>r.Status=="Succeeded").Sum(r=>r.AmountCents)!=payment.AmountCents)
                return new(null,ReturnError.Conflict,"Completion requires a fully settled refund without unresolved refunds.");
        }
        var now=DateTime.UtcNow;value.Status=request.Status;
        switch(request.Status) {case "Approved":value.ApprovedAt=now;break;case "Received":value.ReceivedAt=now;break;case "Completed":value.CompletedAt=now;break;}
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(value);
    }
    public async Task<RefundResult> CreateRefundAsync(Guid id,long amount,string? key,CancellationToken ct)
    {
        if(amount<=0||string.IsNullOrWhiteSpace(key)||key.Length>100) return new(null,Error:"Invalid refund amount or idempotency key");
        var value=await db.ReturnRequests.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct);
        if(value is null) return new(null,Error:"Return not found");
        if(value.Status!="Received" &&
            (value.Status!="Completed" || !await db.Refunds.AsNoTracking().AnyAsync(r=>r.PaymentId==value.PaymentId&&r.IdempotencyKey==key,ct)))
            return new(null,Error:"Return must be Received before creating a new refund");
        if(!await db.Payments.AsNoTracking().AnyAsync(p=>p.Id==value.PaymentId&&p.OrderId==value.OrderId,ct))
            return new(null,Error:"Return not found");
        // RefundService owns its transaction and serializes all refunds on the payment row.
        return await refunds.Create(value.PaymentId,amount,key);
    }

    public Task<ReturnResult> UpdateStatusAsync(Guid id,UpdateReturnStatusRequest request,CancellationToken ct) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("return.update","sqlserver",()=>UpdateStatusAsyncMeasuredCore(id,request,ct),r=>r.Error is null?(r.Replayed?"replayed":"success"):r.Error==ReturnError.InvalidRequest?"invalid":r.Error==ReturnError.NotFound?"not_found":"conflict");
    public Task<ReturnResult> CreateAsync(string customerId,Guid orderId,string? key,CreateReturnRequest request,CancellationToken ct) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("return.create","sqlserver",()=>CreateAsyncMeasuredCore(customerId,orderId,key,request,ct),r=>r.Error is null?(r.Replayed?"replayed":"success"):r.Error==ReturnError.InvalidRequest?"invalid":r.Error==ReturnError.NotFound?"not_found":"conflict");
}
