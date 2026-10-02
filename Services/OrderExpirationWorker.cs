using Microsoft.EntityFrameworkCore;

namespace Ecommerce;

public class OrderExpirationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<OrderExpirationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunBatchAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex, "Order expiration batch failed");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    // Internal so verification can exercise the real batch without waiting for the timer.
    internal async Task RunBatchAsync(DateTime nowUtc, CancellationToken stoppingToken = default)
    {
        stoppingToken.ThrowIfCancellationRequested();
        var cutoff = nowUtc.AddMinutes(-15);

        List<Guid> orderIds;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ShopDb>();

            orderIds = await db.Orders
                .Where(o =>
                    o.Status == "PendingPayment" &&
                    o.CreatedAt <= cutoff &&
                    !db.Payments.Any(p =>
                        p.OrderId == o.Id &&
                        (p.Status == "Pending" ||
                         p.Status == "Unknown")))
                .OrderBy(o => o.CreatedAt)
                .ThenBy(o => o.Id)
                .Select(o => o.Id)
                .Take(100)
                .ToListAsync(stoppingToken);
        }

        foreach (var orderId in orderIds)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                await using var scope =
                    scopeFactory.CreateAsyncScope();

                var service = scope.ServiceProvider
                    .GetRequiredService<OrderService>();

                var result = await service.Expire(
                    orderId, nowUtc);

                if (result.Error is not null)
                {
                    logger.LogDebug(
                        "Skipped order {OrderId}: {Reason}",
                        orderId, result.Error);
                }
                else if (!result.Replayed)
                {
                    logger.LogInformation(
                        "Expired order {OrderId}", orderId);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to expire order {OrderId}",
                    orderId);
            }
        }
    }

}