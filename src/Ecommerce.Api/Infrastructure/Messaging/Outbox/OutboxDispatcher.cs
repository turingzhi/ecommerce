using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Observability;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Messaging.Outbox;

public class OutboxDispatcher(
    ShopDbContext db,
    IEventPublisher publisher)
{
    public async Task DispatchBatchAsync(
        CancellationToken cancellationToken)
    {
        var messages = await db.Outbox
            .Where(m => m.PublishedAt == null &&
                        !m.DeadLettered &&
                        (m.NextAttemptAt == null || m.NextAttemptAt <= DateTime.UtcNow))
            .OrderBy(m => m.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                message.PublishedAt = DateTime.UtcNow;
                message.LastError = null;
                message.NextAttemptAt = null;
                await db.SaveChangesAsync(cancellationToken);
                Ecommerce.Observability.CommerceTelemetry.RecordEvent("outbox.publish",message.Type,"success");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                message.AttemptCount++;
                message.LastAttemptAt = DateTime.UtcNow;
                message.LastError = ex.Message;
                message.DeadLettered = ex is not BrokerDeliveryUnavailableException &&
                    message.AttemptCount >= 5;
                var delaySeconds = Math.Min(300, Math.Pow(2, message.AttemptCount));
                message.NextAttemptAt = message.DeadLettered
                    ? null
                    : DateTime.UtcNow.AddSeconds(delaySeconds);
                await db.SaveChangesAsync(cancellationToken);
                Ecommerce.Observability.CommerceTelemetry.RecordEvent("outbox.publish",message.Type,message.DeadLettered?"dead_letter":"retry");
                throw;
            }
        }
    }
}
