namespace Ecommerce.Features.Orders.Contracts;

public record CreateOrderItemRequest(int ProductId, int Quantity);

public record CreateOrderRequest(List<CreateOrderItemRequest> Items);
