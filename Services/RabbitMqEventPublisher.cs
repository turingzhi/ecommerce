using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Ecommerce;

public sealed class RabbitMqEventPublisher(IOptions<RabbitMqOptions> options) : IEventPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var factory = RabbitMqTopology.CreateFactory(options.Value);
            await using var connection = await factory.CreateConnectionAsync(cancellationToken);
            await using var channel = await connection.CreateChannelAsync(
                RabbitMqTopology.ConfirmedChannelOptions(), cancellationToken);

            await RabbitMqTopology.DeclareAsync(channel, cancellationToken);

            var body = JsonSerializer.SerializeToUtf8Bytes(new BrokerEvent(
                message.Id, message.OrderId, message.Type, message.Payload));
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.Id.ToString("D")
            };

            // Confirm tracking also turns an unroutable mandatory publish into an exception.
            // The Outbox dispatcher marks PublishedAt only after this call completes.
            await channel.BasicPublishAsync(RabbitMqTopology.Exchange,
                options.Value.RoutingKey, mandatory: true,
                basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PublishException)
        {
            throw; // Broker rejected or could not route the publication.
        }
        catch (Exception ex)
        {
            // A transport/connection failure leaves delivery uncertain. Keep the
            // Outbox record for later replay even after many failed attempts.
            throw new BrokerDeliveryUnavailableException("RabbitMQ delivery unavailable", ex);
        }
    }
}

public sealed class BrokerDeliveryUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);

public sealed record BrokerEvent(Guid Id, Guid? OrderId, string Type, string Payload)
{
    public OutboxMessage ToOutboxMessage() => new()
    {
        Id = Id,
        OrderId = OrderId,
        Type = Type,
        Payload = Payload
    };
}
