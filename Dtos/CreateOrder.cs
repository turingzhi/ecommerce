namespace Ecommerce.Dtos;

public record CreateOrderItem(int ProductId, int Quantity);

public record CreateOrder(List<CreateOrderItem> Items);
