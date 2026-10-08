using Ecommerce.Features.Orders.Models;

namespace Ecommerce.Features.Orders.Contracts;

public record AdminOrderSummaryResponse(Guid Id, string CustomerId, string Status,
    string Currency, DateTime CreatedAt)
{
    public static AdminOrderSummaryResponse From(Order order) => new(order.Id,
        order.CustomerId, order.Status, order.Currency,
        DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc));
}

public record AdminOrderResponse(Guid Id, string CustomerId, string Status,
    string Currency, DateTime CreatedAt, IReadOnlyList<OrderItemResponse> OrderItems)
{
    public static AdminOrderResponse From(Order order) => new(order.Id,
        order.CustomerId, order.Status, order.Currency,
        DateTime.SpecifyKind(order.CreatedAt, DateTimeKind.Utc),
        order.OrderItems.OrderBy(item => item.ProductId).Select(item =>
            new OrderItemResponse(item.Id, item.ProductId, item.Quantity, item.UnitPriceCents)).ToList());
}

public record AdminOrderListResponse(int Page, int PageSize, long Total,
    IReadOnlyList<AdminOrderSummaryResponse> Orders);
