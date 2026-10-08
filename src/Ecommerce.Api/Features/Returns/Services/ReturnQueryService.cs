using Ecommerce.Common.Pagination;
using Ecommerce.Features.Returns.Contracts;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Features.Returns.Services;
public class ReturnQueryService(ShopDbContext db)
{
    public async Task<ReturnResponse?> GetForOwnerAsync(string customerId,Guid orderId,CancellationToken ct)
    {
        if(!await db.Orders.AsNoTracking().AnyAsync(o=>o.Id==orderId&&o.CustomerId==customerId,ct)) return null;
        var id=await db.ReturnRequests.AsNoTracking().Where(r=>r.OrderId==orderId).Select(r=>(Guid?)r.Id).SingleOrDefaultAsync(ct);
        return id is null?null:await GetByIdAsync(id.Value,ct);
    }
    public async Task<ReturnResponse?> GetByIdAsync(Guid id,CancellationToken ct)
    {
        var identity=await db.ReturnRequests.AsNoTracking().Where(r=>r.Id==id).Select(r=>new {r.PaymentId,r.OrderId}).SingleOrDefaultAsync(ct);
        if(identity is null) return null;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var payment=await db.Payments.FromSqlInterpolated($"SELECT * FROM [Payments] WITH (UPDLOCK,HOLDLOCK) WHERE Id = {identity.PaymentId}").AsNoTracking().SingleOrDefaultAsync(ct);
        var value=await db.ReturnRequests.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct);
        if(value is null||payment is null||payment.OrderId!=value.OrderId) return null;
        var rows=await db.Refunds.AsNoTracking().Where(r=>r.PaymentId==payment.Id).ToListAsync(ct);
        var result=ReturnResponse.From(value,payment,rows.Where(r=>r.Status=="Succeeded").Sum(r=>r.AmountCents),rows.Where(r=>r.Status is "Pending" or "Unknown").Sum(r=>r.AmountCents));
        await tx.CommitAsync(ct);return result;
    }
    public async Task<ReturnListResponse> ListAsync(string? status,int? page,int? pageSize,CancellationToken ct)
    {
        var bounds=PageBounds.Normalize(page,pageSize);
        if(bounds.Offset>int.MaxValue) return new(bounds.Page,bounds.PageSize,[]);
        var query=db.ReturnRequests.AsNoTracking();
        if(status is not null) query=query.Where(r=>r.Status==status);
        var ids=await query.OrderByDescending(r=>r.CreatedAt).ThenByDescending(r=>r.Id).Skip((int)bounds.Offset).Take(bounds.PageSize).Select(r=>r.Id).ToListAsync(ct);
        var values=new List<ReturnResponse>();
        foreach(var id in ids) {var value=await GetByIdAsync(id,ct);if(value is not null&&(status is null||value.Status==status)) values.Add(value);}
        return new(bounds.Page,bounds.PageSize,values);
    }
}
