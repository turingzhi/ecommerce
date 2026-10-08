using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Search;
using Ecommerce.Observability;
using System.Diagnostics;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
namespace Ecommerce.Infrastructure.Messaging.RabbitMq;

public sealed class RabbitMqConsumerWorker(
    IOptions<RabbitMqOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<RabbitMqConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqTopology.CreateFactory(options.Value);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    RabbitMqTopology.ConfirmedChannelOptions(), stoppingToken);
                await RabbitMqTopology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                    await HandleDeliveryAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(RabbitMqTopology.Queue,
                    autoAck: false, consumer: consumer, cancellationToken: stoppingToken);

                logger.LogInformation("RabbitMQ consumer connected");
                while (connection.IsOpen && channel.IsOpen)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RabbitMQ consumer disconnected; reconnecting");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task HandleDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken cancellationToken)
    {
        // RabbitMQ owns the delivery buffer after the callback returns.
        var body = delivery.Body.ToArray();
        var attempts = ReadAttempts(delivery.BasicProperties.Headers);

        Activity? activity=null;
        string eventType="other";
        try
        {
            var message = JsonSerializer.Deserialize<BrokerEvent>(body)
                ?? throw new JsonException("Empty event body");
            if (message.Id == Guid.Empty)
                throw new JsonException("Event ID is required");

            eventType=message.Type;

            activity=CommerceTelemetry.StartMessaging("event.consume",ActivityKind.Consumer,message.TraceParent,message.TraceState);
            activity?.SetTag("messaging.system","rabbitmq").SetTag("messaging.operation","process");
            await using var scope = scopeFactory.CreateAsyncScope();
            var consumer = scope.ServiceProvider.GetRequiredService<EventConsumer>();
            var handled = await consumer.ConsumeAsync(
                message.ToOutboxMessage(), cancellationToken);

            CommerceTelemetry.RecordEvent("event.consume",eventType,handled?"handled":"duplicate");
            if (!handled)
                logger.LogInformation("Ignored duplicate RabbitMQ event {MessageId}", message.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // Closing the channel makes RabbitMQ redeliver the unacknowledged message.
        }
        catch (Exception ex)
        {
            try
            {
                // Search and database outages are infrastructure failures, not
                // poison deliveries. Keep them retryable until they recover.
                var infrastructureUnavailable =
                    ex is ProductSearchUnavailableException || IsDatabaseFailure(ex);
                var nextAttempts = infrastructureUnavailable ? attempts : attempts + 1;
                var exhausted = !infrastructureUnavailable &&
                    nextAttempts >= RabbitMqTopology.MaximumAttempts;
                CommerceTelemetry.RecordEvent("event.consume",eventType,exhausted?"dead_letter":"retry");
                activity?.SetStatus(ActivityStatusCode.Error);
                var properties = new BasicProperties
                {
                    Persistent = true,
                    ContentType = "application/json",
                    MessageId = delivery.BasicProperties.MessageId,
                    Headers = new Dictionary<string, object?>
                    {
                        ["x-attempts"] = nextAttempts,
                        ["x-last-error"] = ex.Message[..Math.Min(ex.Message.Length, 500)]
                    }
                };

                // Confirm the replacement before acknowledging the original.
                await channel.BasicPublishAsync(
                    exhausted ? RabbitMqTopology.DeadExchange : RabbitMqTopology.RetryExchange,
                    RabbitMqTopology.RoutingKey, mandatory: true,
                    basicProperties: properties, body: body,
                    cancellationToken: cancellationToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
                logger.LogWarning(ex, exhausted
                    ? "RabbitMQ event {MessageId} moved to dead-letter queue after {Attempts} attempts"
                    : "RabbitMQ event {MessageId} scheduled for retry {Attempts}",
                    delivery.BasicProperties.MessageId, nextAttempts);
            }
            catch (Exception retryException)
            {
                logger.LogError(retryException,
                    "Could not safely move RabbitMQ event {MessageId}; requeueing original",
                    delivery.BasicProperties.MessageId);
                if (channel.IsOpen)
                    await channel.BasicNackAsync(delivery.DeliveryTag,
                        multiple: false, requeue: true, cancellationToken: cancellationToken);
            }

            return;
        }

        finally { activity?.Dispose(); }

        // This is the only acknowledgement path after successful database processing.
        await channel.BasicAckAsync(delivery.DeliveryTag, false, cancellationToken);
    }

    private static int ReadAttempts(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-attempts", out var value))
            return 0;
        return value switch
        {
            int number when number >= 0 => number,
            long number when number >= 0 && number <= int.MaxValue => (int)number,
            _ => RabbitMqTopology.MaximumAttempts - 1
        };
    }

    private static bool IsDatabaseFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null;
             current = current.InnerException)
        {
            if (current is DbException or DbUpdateException)
                return true;
        }

        return false;
    }
}
