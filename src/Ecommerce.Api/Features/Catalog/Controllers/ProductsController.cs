using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Infrastructure.Search;
using Ecommerce.Observability;
using System.Text.Json;
using StackExchange.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecommerce.Features.Catalog.Controllers;

[ApiController]
[Route("products")]
[AllowAnonymous]
public sealed class ProductsController : ControllerBase
{
    [HttpGet("search")]
    [EnableRateLimiting(CommerceRateLimiting.SearchPolicy)]
    public async Task<IResult> Search(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] long? minPriceCents,
        [FromQuery] long? maxPriceCents,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? sort,
        [FromServices] IConnectionMultiplexer redis,
        [FromServices] ProductSearchService search,
        [FromServices] ILogger<ProductSearchService> logger,
        CancellationToken cancellationToken)
    {
        if (!ProductSearchParameters.TryCreate(
            q, category, minPriceCents, maxPriceCents, page, pageSize, sort,
            out var parameters, out var error))
            return Results.BadRequest(new { error });

        try
        {
            using var operation = CommerceTelemetry.StartOperation("search", "elasticsearch");
            var cache = redis.GetDatabase();
            string? key = null;

            // Try Redis first.
            try
            {
                using var redisRead = CommerceTelemetry.Source.StartActivity("redis.search.read");
                redisRead?.SetTag("commerce.dependency", "redis");
                var generation = await cache.StringGetAsync("products:search:generation");

                key = parameters!.CacheKey(generation.ToString());
                var cached = await cache.StringGetAsync(key);
                if (cached.HasValue)
                {
                    var response =
                        JsonSerializer.Deserialize<ProductPageResponse>(
                            cached.ToString());

                    if (response is not null)
                    {
                        CommerceTelemetry.RecordCache("hit"); operation.Complete("success");
                        logger.LogInformation("Search cache HIT");
                        return Results.Ok(response);
                    }
                }
            }
            catch (RedisException exception)
            {
                CommerceTelemetry.RecordCache("error");
                logger.LogWarning(exception, "Redis read failed; continuing search");
            }

            // On a miss or Redis outage, use Elasticsearch.
            CommerceTelemetry.RecordCache("miss");
            logger.LogInformation("Search cache MISS");

            var found = await search.SearchAsync(parameters!, cancellationToken);
            var results = new ProductPageResponse(
                found.Products.Select(ProductSummaryResponse.From).ToList(),
                found.Total, parameters!.Page, parameters.PageSize);

            // Store the results in Redis.
            if (key is not null)
            {
                try
                {
                    using var redisWrite = CommerceTelemetry.Source.StartActivity("redis.search.write");
                    redisWrite?.SetTag("commerce.dependency", "redis");
                    await cache.StringSetAsync(
                        key,
                        JsonSerializer.Serialize(results),
                        TimeSpan.FromSeconds(30));
                }
                catch (RedisException exception)
                {
                    logger.LogWarning(exception, "Redis write failed");
                }
            }

            operation.Complete("success");
            return Results.Ok(results);
        }
        catch (ProductSearchUnavailableException exception)
        {
            logger.LogError(exception, "Product search is unavailable");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Product search is temporarily unavailable.",
                detail: "Please try again later.");
        }
    }

    [HttpGet("")]
    [EnableRateLimiting(CommerceRateLimiting.SearchPolicy)]
    public async Task<IResult> List(
        [FromQuery] string? category,
        [FromQuery] long? minPriceCents,
        [FromQuery] long? maxPriceCents,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? sort,
        [FromServices] ProductQueryService service,
        CancellationToken ct)
    {
        if (!ProductBrowseParameters.TryCreate(
            category, minPriceCents, maxPriceCents, page, pageSize, sort,
            out var value, out var error))
            return Results.BadRequest(new { error });
        return Results.Ok(await service.BrowseAsync(value!, ct));
    }

    [HttpGet("{productId:int}")]
    [EnableRateLimiting(CommerceRateLimiting.SearchPolicy)]
    public async Task<IResult> GetById(
        [FromRoute] int productId,
        [FromServices] ProductQueryService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var value = await service.GetAsync(productId, ct);
        return value is null ? Results.NotFound() : Results.Ok(value);
    }
}
