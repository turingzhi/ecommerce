using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Features.Shipments.Services;

public class ShipmentEventHandler(ShopDbContext db)
{
    private sealed record OrderPaidPayload(Guid OrderId, Guid PaymentId);

    public async Task<bool> HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.Id == Guid.Empty) throw new JsonException("Paid event has no identity");
        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == message.Id, cancellationToken)) return false;
        var payload = JsonSerializer.Deserialize<OrderPaidPayload>(message.Payload)
            ?? throw new JsonException("Paid event has no payload");
        if (payload.OrderId == Guid.Empty || payload.PaymentId == Guid.Empty || message.OrderId != payload.OrderId)
            throw new JsonException("Paid event identifiers are missing or inconsistent");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // All deliveries for this order serialize here, including distinct event IDs.
        var order = await db.Orders.FromSqlInterpolated(
            $"SELECT * FROM [Orders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {payload.OrderId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (await db.ProcessedMessages.AnyAsync(m => m.MessageId == message.Id, cancellationToken)) return false;
        if (order?.Status != "Paid" || !await db.Payments.AnyAsync(p =>
            p.Id == payload.PaymentId && p.OrderId == payload.OrderId && p.Status == "Succeeded", cancellationToken))
            throw new JsonException("Paid event does not match a paid order and successful payment");
        if (!await db.Shipments.AnyAsync(s => s.OrderId == payload.OrderId, cancellationToken))
        {
            var shipment=new Shipment {OrderId=payload.OrderId};
            db.Shipments.Add(shipment);
            db.ShipmentHistory.Add(new ShipmentHistory {ShipmentId=shipment.Id,ToStatus="Pending",OccurredAt=shipment.CreatedAt});
        }
        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = message.Id });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
