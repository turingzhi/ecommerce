using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Cart.Contracts;
using Ecommerce.Features.Cart.Services;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Infrastructure.Persistence;
using System.Security.Claims;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecommerce.Features.Cart.Controllers;

[ApiController]
[Route("cart")]
[Authorize]
public sealed class CartController : ControllerBase
{
    [HttpGet("")]
    public async Task<IResult> Get(
        [FromServices] CartService service,
        [FromServices] ILogger<CartService> logger,
        CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        return await HandleRedisFailure(async () =>
            Results.Ok(await service.GetAsync(customerId, cancellationToken)), logger);
    }

    [HttpPost("checkout")]
    [EnableRateLimiting(CommerceRateLimiting.OrderPolicy)]
    public async Task<IResult> Checkout(
        [FromServices] CartService cartService,
        [FromServices] OrderService orders,
        [FromServices] ShopDbContext db,
        [FromServices] ILogger<CartService> logger,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.BadRequest(new { error = "Invalid Idempotency Key" });

        // Checkout keys identify a completed operation, even after the cart
        // changes or expires. SQL replay must not depend on Redis availability.
        var existing = await db.Orders.AsNoTracking()
            .Include(order => order.OrderItems)
            .SingleOrDefaultAsync(order => order.CustomerId == customerId &&
                order.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
            return Results.Ok(OrderResponse.From(existing));

        return await HandleRedisFailure(async () =>
        {
            var snapshot = await cartService.GetAsync(customerId, cancellationToken);
            if (snapshot.Items.Count < 1 || snapshot.Items.Count > CartService.MaximumItems ||
                snapshot.Items.Any(item => item.ProductId <= 0 || item.Quantity < 1 || item.Quantity > 100))
                return Results.BadRequest(new { error = "Cart must contain 1 to 100 valid items." });
            var request = new CreateOrderRequest(snapshot.Items
                .Select(item => new CreateOrderItemRequest(item.ProductId, item.Quantity)).ToList());
            // Existing SQL transaction owns stock, current prices, and concurrent
            // key replay. Preserve the cart, including edits made during checkout.
            var result = await orders.Create(customerId, key, request);
            if (result.Error is not null)
                return Results.Conflict(new { error = result.Error });
            return result.Replayed
                ? Results.Ok(OrderResponse.From(result.Order!))
                : Results.Json(OrderResponse.From(result.Order!),
                    statusCode: StatusCodes.Status201Created);
        }, logger);
    }

    [HttpPut("items/{productId:int}")]
    public async Task<IResult> SetItem(
        [FromRoute] int productId,
        [FromBody] SetCartItemRequest request,
        [FromServices] CartService service,
        [FromServices] ILogger<CartService> logger,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        if (productId <= 0 || request.Quantity < 1 || request.Quantity > 100)
            return Results.BadRequest(new { error = "ProductId must be positive and quantity must be between 1 and 100." });
        return await HandleRedisFailure(async () =>
        {
            var result = await service.SetAsync(customerId, productId, request.Quantity, cancellationToken);
            return result switch
            {
                CartWriteResult.ProductNotFound => Results.NotFound(),
                CartWriteResult.TooManyItems => Results.Conflict(new { error = "Cart may contain at most 100 distinct products." }),
                _ => Results.NoContent()
            };
        }, logger);
    }

    [HttpDelete("items/{productId:int}")]
    public async Task<IResult> RemoveItem(
        [FromRoute] int productId,
        [FromServices] CartService service,
        [FromServices] ILogger<CartService> logger,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        if (productId <= 0)
            return Results.BadRequest(new { error = "ProductId must be positive." });
        return await HandleRedisFailure(async () =>
        {
            await service.RemoveAsync(customerId, productId, cancellationToken);
            return Results.NoContent();
        }, logger);
    }

    [HttpDelete("")]
    public async Task<IResult> Clear(
        [FromServices] CartService service,
        [FromServices] ILogger<CartService> logger,
        CancellationToken cancellationToken)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();
        return await HandleRedisFailure(async () =>
        {
            await service.ClearAsync(customerId, cancellationToken);
            return Results.NoContent();
        }, logger);
    }

    private static async Task<IResult> HandleRedisFailure(
        Func<Task<IResult>> operation, ILogger<CartService> logger)
    {
        try
        {
            return await operation();
        }
        catch (RedisException exception)
        {
            logger.LogWarning(exception, "Shopping cart Redis operation failed");
            return Results.Json(new { error = "Shopping cart is temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
