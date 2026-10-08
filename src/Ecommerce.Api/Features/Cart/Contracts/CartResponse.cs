namespace Ecommerce.Features.Cart.Contracts;

public record CartItemResponse(int ProductId, int Quantity);
public record CartResponse(IReadOnlyList<CartItemResponse> Items);
