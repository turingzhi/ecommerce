using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Elastic.Clients.Elasticsearch;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Ecommerce.Infrastructure.Health;

public sealed class SqlServerHealthCheck(ShopDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy();
}

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}

public sealed class RabbitMqHealthCheck(IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var factory = RabbitMqTopology.CreateFactory(options.Value);
        factory.RequestedConnectionTimeout = TimeSpan.FromSeconds(3);
        // Use a short-lived connection; do not declare topology or publish events.
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}

public sealed class ElasticsearchHealthCheck(ElasticsearchClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var response = await client.Cluster.HealthAsync(cancellationToken: cancellationToken);
        // Yellow is expected for a single node with unassigned replica shards.
        return response.IsValidResponse
               && !response.TimedOut
               && response.Status.ToString().Equals("red", StringComparison.OrdinalIgnoreCase) == false
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy();
    }
}
