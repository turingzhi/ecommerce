namespace Ecommerce.Features.Orders.Contracts;

public record OrderSummaryResponse(
    Guid Id,
    string Status,
    DateTime CreatedAt,
    string Currency);

public record OrderListResponse(
    int Page,
    int PageSize,
    IReadOnlyList<OrderSummaryResponse> Orders);
