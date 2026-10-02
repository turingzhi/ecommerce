using Microsoft.EntityFrameworkCore;

namespace Ecommerce;

public class EventConsumer(ShopDb db)
{
    public async Task<bool> ConsumeAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        var alreadyProcessed = await db.ProcessedMessages
            .AnyAsync(p => p.MessageId == message.Id, cancellationToken);

        if (alreadyProcessed)
        {
            return false;
        }

        // This is the local consumer's business-effect placeholder.
        // Record the message in the same unit of work as a future effect.
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = message.Id
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
