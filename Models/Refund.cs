namespace Ecommerce;

public class Refund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public long AmountCents { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "Pending";
    public string IdempotencyKey { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}