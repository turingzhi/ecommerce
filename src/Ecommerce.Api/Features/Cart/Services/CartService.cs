using Ecommerce.Features.Cart.Contracts;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Observability;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Ecommerce.Features.Cart.Services;

public enum CartWriteResult { Success, ProductNotFound, TooManyItems }

public sealed class CartService(IConnectionMultiplexer redis, ShopDbContext db)
{
    public const int MaximumItems = 100;
    public const int ExpirationSeconds = 7 * 24 * 60 * 60;

    private const string ReadScript = """
        local items = redis.call('HGETALL', KEYS[1])
        if #items > 0 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        return items
        """;

    private const string SetScript = """
        if redis.call('HEXISTS', KEYS[1], ARGV[1]) == 0
           and redis.call('HLEN', KEYS[1]) >= tonumber(ARGV[3]) then
            return 0
        end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('EXPIRE', KEYS[1], ARGV[4])
        return 1
        """;

    private const string RemoveScript = """
        redis.call('HDEL', KEYS[1], ARGV[1])
        if redis.call('EXISTS', KEYS[1]) == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[2])
        end
        return 1
        """;

    private static RedisKey Key(string customerId) => $"cart:v1:{customerId}";

    private async Task<CartResponse> GetAsyncMeasuredCore(string customerId, CancellationToken cancellationToken)
    {
        // Read and refresh inactivity atomically; an empty cart creates no key.
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            ReadScript, [Key(customerId)], [ExpirationSeconds]).WaitAsync(cancellationToken);
        var entries = (RedisResult[]?)result ?? [];
        var items = new List<CartItemResponse>();
        for (var index = 0; index < entries.Length; index += 2)
            items.Add(new CartItemResponse(
                int.Parse(entries[index].ToString(), CultureInfo.InvariantCulture),
                int.Parse(entries[index + 1].ToString(), CultureInfo.InvariantCulture)));
        return new CartResponse(items.OrderBy(item => item.ProductId).ToList());
    }

    private async Task<CartWriteResult> SetAsyncMeasuredCore(
        string customerId, int productId, int quantity, CancellationToken cancellationToken)
    {
        if (!await db.Products.AnyAsync(product => product.Id == productId, cancellationToken))
            return CartWriteResult.ProductNotFound;
        // The cap, quantity replacement, and TTL are one atomic operation.
        // Stock is deliberately checked only by the SQL-backed checkout flow.
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            SetScript, [Key(customerId)],
            [productId, quantity, MaximumItems, ExpirationSeconds]).WaitAsync(cancellationToken);
        return (long)result == 1 ? CartWriteResult.Success : CartWriteResult.TooManyItems;
    }

    private async Task RemoveAsyncCore(string customerId, int productId, CancellationToken cancellationToken)
    {
        await redis.GetDatabase().ScriptEvaluateAsync(
            RemoveScript, [Key(customerId)], [productId, ExpirationSeconds]).WaitAsync(cancellationToken);
    }

    private async Task ClearAsyncCore(string customerId, CancellationToken cancellationToken)
    {
        await redis.GetDatabase().KeyDeleteAsync(Key(customerId)).WaitAsync(cancellationToken);
    }

    public async Task RemoveAsync(string customerId,int productId,CancellationToken cancellationToken) => await Ecommerce.Observability.CommerceTelemetry.MeasureAsync("cart.write","redis",async()=>{await RemoveAsyncCore(customerId,productId,cancellationToken);return true;},_=>"success");
    public async Task ClearAsync(string customerId,CancellationToken cancellationToken) => await Ecommerce.Observability.CommerceTelemetry.MeasureAsync("cart.write","redis",async()=>{await ClearAsyncCore(customerId,cancellationToken);return true;},_=>"success");
    public Task<CartWriteResult> SetAsync(
        string customerId, int productId, int quantity, CancellationToken cancellationToken) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("cart.write","redis",()=>SetAsyncMeasuredCore(customerId,productId,quantity,cancellationToken),r=>r==CartWriteResult.Success?"success":"conflict");
    public Task<CartResponse> GetAsync(string customerId, CancellationToken cancellationToken) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("cart.read","redis",()=>GetAsyncMeasuredCore(customerId,cancellationToken),_=>"success");
}
