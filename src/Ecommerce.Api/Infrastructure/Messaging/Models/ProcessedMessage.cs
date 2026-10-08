namespace Ecommerce.Infrastructure.Messaging.Models;

public class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
