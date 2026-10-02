namespace Ecommerce;

public class LoggingEventPublisher(
    ILogger<LoggingEventPublisher> logger,
    EventConsumer consumer) : IEventPublisher
{
    public Task PublishAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Published event {MessageId}: {EventType} for order {OrderId}. Payload: {Payload}",
            message.Id,
            message.Type,
            message.OrderId,
            message.Payload);

        return PublishToConsumerAsync(message, cancellationToken);
    }

    private async Task PublishToConsumerAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var handled = await consumer.ConsumeAsync(message, cancellationToken);
        if (!handled)
        {
            logger.LogInformation(
                "Ignored duplicate event {MessageId}", message.Id);
        }
    }
}
