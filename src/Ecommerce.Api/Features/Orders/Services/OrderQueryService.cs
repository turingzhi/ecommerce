using Ecommerce.Common.Pagination;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Features.Orders.Services;

public class OrderQueryService(ShopDbContext db)
{
    public async Task<AdminOrderListResponse> ListForAdminAsync(string? status, PageBounds bounds, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (status is not null) query = query.Where(order => order.Status == status);
        var total = await query.LongCountAsync(ct);
        if (bounds.Offset > int.MaxValue) return new(bounds.Page, bounds.PageSize, total, []);
        var orders = await query.OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id).Skip((int)bounds.Offset).Take(bounds.PageSize).ToListAsync(ct);
        return new(bounds.Page, bounds.PageSize, total, orders.Select(AdminOrderSummaryResponse.From).ToList());
    }

    public async Task<AdminOrderResponse?> GetForAdminAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(order => order.OrderItems)
            .SingleOrDefaultAsync(order => order.Id == id, ct);
        return order is null ? null : AdminOrderResponse.From(order);
    }
}
