using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Infrastructure.Persistence;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecommerce.Features.Orders.Controllers;

[ApiController]
[Route("orders")]
[Authorize]
public sealed class OrdersController : ControllerBase
{
    [HttpPost("")]
    [EnableRateLimiting(CommerceRateLimiting.OrderPolicy)]
    public async Task<IResult> Create(
        [FromBody] CreateOrderRequest request,
        [FromServices] OrderService service)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();

        var key = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.BadRequest(new { error = "Invalid Idempotency Key" });
        if (request.Items is null || request.Items.Any(item => item is null))
            return Results.BadRequest(new { error = "Items or item entry is null" });
        if (request.Items.Count < 1 || request.Items.Count > 100)
            return Results.BadRequest(new { error = "the number of items is smaller than 1 or bigger than 100" });
        if (request.Items.Any(item => item.ProductId <= 0))
            return Results.BadRequest(new { error = "ProductId should be positive" });
        if (request.Items.Any(item => item.Quantity < 1 || item.Quantity > 100))
            return Results.BadRequest(new { error = "Quantity should be between 1 and 100" });

        var uniqueProductIds = request.Items.Select(item => item.ProductId).Distinct();
        if (uniqueProductIds.Count() != request.Items.Count)
            return Results.BadRequest(new { error = "There are some duplicate productIds" });

        var result = await service.Create(customerId, key, request);
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });
        return result.Replayed
            ? Results.Ok(OrderResponse.From(result.Order!))
            : Results.Json(OrderResponse.From(result.Order!), statusCode: StatusCodes.Status201Created);
    }

    [HttpPost("{orderId:guid}/cancel")]
    public async Task<IResult> Cancel(
        [FromRoute] Guid orderId,
        [FromServices] OrderService service)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();

        var result = await service.Cancel(customerId, orderId);
        if (result.Error == "Order not found")
            return Results.NotFound();
        if (result.Error is not null)
            return Results.Conflict(new { error = result.Error });

        return Results.Ok(OrderResponse.From(result.Order!));
    }

    [HttpGet("")]
    public async Task<IResult> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ShopDbContext db)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();

        var currentPage = Math.Max(page ?? 1, 1);
        var currentPageSize = Math.Clamp(pageSize ?? 10, 1, 50);
        var skip = (currentPage - 1) * currentPageSize;

        var orders = await db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(o => new OrderSummaryResponse(
                o.Id, o.Status, o.CreatedAt, o.Currency))
            .ToListAsync();

        return Results.Ok(new OrderListResponse(
            currentPage, currentPageSize, orders));
    }

    [HttpGet("{id:guid}")]
    public async Task<IResult> GetById(
        [FromRoute] Guid id,
        [FromServices] ShopDbContext db)
    {
        var customerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(customerId))
            return Results.Unauthorized();

        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync(o => o.Id == id && o.CustomerId == customerId);

        return order is null
            ? Results.NotFound()
            : Results.Ok(OrderResponse.From(order));
    }
}
