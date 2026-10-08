using Ecommerce.Common.Pagination;
using Ecommerce.Features.Shipments.Contracts;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Features.Shipments.Services;
public class ShipmentQueryService(ShopDbContext db)
{
    public async Task<ShipmentListResponse> ListAsync(string? status,int? page,int? pageSize,CancellationToken cancellationToken)
    {
        var bounds=PageBounds.Normalize(page,pageSize);
        if(bounds.Offset>int.MaxValue) return new(bounds.Page,bounds.PageSize,[]);
        var query=db.Shipments.AsNoTracking();
        if(status is not null) query=query.Where(s=>s.Status==status);
        var rows=await query.OrderByDescending(s=>s.CreatedAt).ThenByDescending(s=>s.Id)
            .Skip((int)bounds.Offset).Take(bounds.PageSize).ToListAsync(cancellationToken);
        return new(bounds.Page,bounds.PageSize,rows.Select(ShipmentResponse.From).ToList());
    }
    public async Task<ShipmentHistoryResponse?> GetHistoryAsync(Guid id,CancellationToken ct)
    {
        if(!await db.Shipments.AsNoTracking().AnyAsync(s=>s.Id==id,ct)) return null;
        var rows=await db.ShipmentHistory.AsNoTracking().Where(h=>h.ShipmentId==id).OrderBy(h=>h.Id).ToListAsync(ct);
        return new(id,rows.Select(ShipmentHistoryEntryResponse.From).ToList());
    }

    public async Task<OrderTrackingResponse?> GetTrackingAsync(string customerId,Guid orderId,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var order=await db.Orders.AsNoTracking().SingleOrDefaultAsync(o=>o.Id==orderId && o.CustomerId==customerId,ct);
        if(order is null) return null;
        var shipment=await db.Shipments.FromSqlInterpolated($"SELECT * FROM [Shipments] WITH (UPDLOCK, HOLDLOCK) WHERE [OrderId] = {orderId}").AsNoTracking().SingleOrDefaultAsync(ct);
        var history=new List<CustomerShipmentHistoryEntry>();
        if(shipment is not null)
        {
            var rows=await db.ShipmentHistory.AsNoTracking().Where(h=>h.ShipmentId==shipment.Id).OrderBy(h=>h.Id).ToListAsync(ct);
            history=rows.Select(h=>new CustomerShipmentHistoryEntry(h.FromStatus,h.ToStatus,DateTime.SpecifyKind(h.OccurredAt,DateTimeKind.Utc))).ToList();
        }
        await tx.CommitAsync(ct);
        return new(order.Id,order.Status,shipment is null?null:ShipmentResponse.From(shipment),history);
    }
}
