namespace Ecommerce.Features.Orders.Models;

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CustomerId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public List<OrderItem> OrderItems { get; set; } = [];
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "PendingPayment";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
