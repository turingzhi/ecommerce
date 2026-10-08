using Ecommerce.Common.Pagination;
using Ecommerce.Features.Payments.Contracts;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Features.Payments.Services;

public class PaymentQueryService(ShopDbContext db)
{
    public async Task<AdminPaymentListResponse> ListForAdminAsync(string? status, Guid? orderId, PageBounds bounds, CancellationToken ct)
    {
        var query = from payment in db.Payments.AsNoTracking()
                    join order in db.Orders.AsNoTracking() on payment.OrderId equals order.Id
                    select new { Payment = payment, order.CustomerId };
        if (status is not null) query = query.Where(row => row.Payment.Status == status);
        if (orderId is not null) query = query.Where(row => row.Payment.OrderId == orderId);
        var total = await query.LongCountAsync(ct);
        if (bounds.Offset > int.MaxValue) return new(bounds.Page, bounds.PageSize, total, []);
        var rows = await query.OrderByDescending(row => row.Payment.CreatedAt)
            .ThenByDescending(row => row.Payment.Id).Skip((int)bounds.Offset).Take(bounds.PageSize).ToListAsync(ct);
        return new(bounds.Page, bounds.PageSize, total,
            rows.Select(row => AdminPaymentResponse.From(row.Payment, row.CustomerId)).ToList());
    }

    public async Task<AdminPaymentResponse?> GetForAdminAsync(Guid id, CancellationToken ct)
    {
        var row = await (from payment in db.Payments.AsNoTracking()
                         join order in db.Orders.AsNoTracking() on payment.OrderId equals order.Id
                         where payment.Id == id
                         select new { Payment = payment, order.CustomerId }).SingleOrDefaultAsync(ct);
        return row is null ? null : AdminPaymentResponse.From(row.Payment, row.CustomerId);
    }
}
