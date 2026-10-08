using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ecommerce.Common.RateLimiting;

public sealed class CommerceRateLimitOptions
{
    [Range(1, 100_000)]
    public int SearchPermitLimit { get; set; } = 120;
    [Range(1, 100_000)]
    public int OrderPermitLimit { get; set; } = 30;
    [Range(1, 100_000)]
    public int PaymentPermitLimit { get; set; } = 20;
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

public static class CommerceRateLimiting
{
    public const string SearchPolicy = "product-search";
    public const string OrderPolicy = "order-create";
    public const string PaymentPolicy = "payment-create";

    public static IServiceCollection AddCommerceRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CommerceRateLimitOptions>()
            .Bind(configuration.GetSection("RateLimiting"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                        1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests. Please retry later." },
                    cancellationToken: cancellationToken);
            };
            options.AddPolicy(SearchPolicy, context =>
            {
                var limits = context.RequestServices.GetRequiredService<IOptions<CommerceRateLimitOptions>>().Value;
                var address = context.Connection.RemoteIpAddress;
                if (address?.IsIPv4MappedToIPv6 == true)
                    address = address.MapToIPv4();
                // Forwarded headers are not trusted in the current deployment.
                return FixedWindow(address?.ToString() ?? "unknown", limits.SearchPermitLimit, limits.WindowSeconds);
            });
            options.AddPolicy(OrderPolicy, context => CustomerWindow(context, payment: false));
            options.AddPolicy(PaymentPolicy, context => CustomerWindow(context, payment: true));
        });
        return services;
    }

    private static RateLimitPartition<string> CustomerWindow(HttpContext context, bool payment)
    {
        var customerId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        // Authorization runs before the limiter; handlers also reject missing IDs.
        if (string.IsNullOrEmpty(customerId))
            return RateLimitPartition.GetNoLimiter("missing-customer");
        var limits = context.RequestServices.GetRequiredService<IOptions<CommerceRateLimitOptions>>().Value;
        return FixedWindow(customerId,
            payment ? limits.PaymentPermitLimit : limits.OrderPermitLimit, limits.WindowSeconds);
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits, int seconds) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromSeconds(seconds),
            AutoReplenishment = true,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
}
