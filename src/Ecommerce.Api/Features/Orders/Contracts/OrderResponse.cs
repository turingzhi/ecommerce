using Ecommerce.Features.Orders.Models;
namespace Ecommerce.Features.Orders.Contracts;

public record OrderItemResponse(
    Guid Id,
    int ProductId,
    long Quantity,
    long UnitPriceCents);

public record OrderResponse(
    Guid Id,
    string Status,
    string Currency,
    DateTime CreatedAt,
    IReadOnlyList<OrderItemResponse> OrderItems)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.Status,
        order.Currency,
        order.CreatedAt,
        order.OrderItems.Select(item => new OrderItemResponse(
            item.Id,
            item.ProductId,
            item.Quantity,
            item.UnitPriceCents)).ToList());
}
