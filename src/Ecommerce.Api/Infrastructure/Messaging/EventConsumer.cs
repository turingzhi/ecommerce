using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Shipments.Services;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using StackExchange.Redis;

namespace Ecommerce.Infrastructure.Messaging;

public class EventConsumer(ShopDbContext db, IConnectionMultiplexer? redis = null, ProductSearchService? search = null)
{
    public async Task<bool> ConsumeAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Type == "OrderPaid")
            return await new ShipmentEventHandler(db).HandleAsync(message, cancellationToken);

        var alreadyProcessed = await db.ProcessedMessages
            .AnyAsync(p => p.MessageId == message.Id, cancellationToken);

        if (alreadyProcessed)
        {
            return false;
        }

        if (message.Type == "ProductUpserted")
        {
            if (search is null)
                throw new InvalidOperationException("Product search is not configured");

            var snapshot = JsonSerializer.Deserialize<ProductSearchDocument>(message.Payload)
                ?? throw new JsonException("Product event has no snapshot");
            if (snapshot.Id <= 0 || snapshot.Version <= 0)
                throw new JsonException("Product event has invalid identity or version");

            await search.EnsureIndexAsync(cancellationToken);
            await search.IndexDocumentAsync(snapshot, cancellationToken);
            if (redis is not null)
            {
                await redis.GetDatabase()
                    .StringIncrementAsync("products:search:generation");
            }
        }

        // For product events the Elasticsearch write precedes this marker. A
        // repeat after a failed marker is safe because the version is stable.
        db.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = message.Id
        });

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
