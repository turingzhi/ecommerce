namespace Ecommerce;

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Type { get; set; } = "OrderCreated";
    public string Payload { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public bool DeadLettered { get; set; }
}
