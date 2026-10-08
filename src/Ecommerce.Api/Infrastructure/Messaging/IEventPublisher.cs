using Ecommerce.Infrastructure.Messaging.Models;
namespace Ecommerce.Infrastructure.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(
        OutboxMessage message,
        CancellationToken cancellationToken);
}