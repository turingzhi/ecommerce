namespace Ecommerce.Features.Orders.Models;

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ProductId { get; set; }
    public long Quantity { get; set; }
    public long UnitPriceCents { get; set; }
    public Guid OrderId { get; set; }
}
