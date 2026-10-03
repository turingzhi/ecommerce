using RabbitMQ.Client;

namespace Ecommerce;

internal static class RabbitMqTopology
{
    public const string Exchange = "ecommerce.events";
    public const string RoutingKey = "commerce.event";
    public const string Queue = "ecommerce.events.consumer";
    public const string RetryExchange = "ecommerce.events.retry";
    public const string RetryQueue = "ecommerce.events.retry.consumer";
    public const string DeadExchange = "ecommerce.events.dead";
    public const string DeadQueue = "ecommerce.events.dead.consumer";
    public const int MaximumAttempts = 5;

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct,
            durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Direct,
            durable: true, autoDelete: false, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false,
            autoDelete: false, arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = 2000,
                ["x-dead-letter-exchange"] = Exchange,
                ["x-dead-letter-routing-key"] = RoutingKey
            }, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(RetryQueue, RetryExchange, RoutingKey,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false,
            autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, RoutingKey,
            cancellationToken: cancellationToken);
    }

    public static ConnectionFactory CreateFactory(RabbitMqOptions options) => new()
    {
        HostName = options.HostName,
        Port = options.Port,
        UserName = options.UserName,
        Password = options.Password,
        AutomaticRecoveryEnabled = false
    };

    public static CreateChannelOptions ConfirmedChannelOptions() => new(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true);
}
