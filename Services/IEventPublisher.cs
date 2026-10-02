namespace Ecommerce;

public interface IEventPublisher
{
    Task PublishAsync(
        OutboxMessage message,
        CancellationToken cancellationToken);
}