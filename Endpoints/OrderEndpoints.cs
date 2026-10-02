using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce;

public static class OrderEndpoints
{
    public static WebApplication MapOrderEndpoints(this WebApplication app)
    {
        app.MapPost("/orders", async (
            CreateOrder request,
            HttpRequest http,
            OrderService service,
            ClaimsPrincipal user) =>
        {
            var customerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(customerId))
                return Results.Unauthorized();

            var key = http.Headers["Idempotency-Key"].ToString();
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
                ? Results.Ok(result.Order)
                : Results.Json(result.Order, statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization();

        app.MapGet("/orders", async (
            int? page,
            int? pageSize,
            ShopDb db,
            ClaimsPrincipal user) =>
        {
            var customerId = user.FindFirstValue(ClaimTypes.NameIdentifier);

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
                .Select(o => new
                {
                    o.Id,
                    o.Status,
                    o.CreatedAt,
                    o.Currency
                })
                .ToListAsync();

            return Results.Ok(new
            {
                Page = currentPage,
                PageSize = currentPageSize,
                Orders = orders
            });
        })
        .RequireAuthorization();

        app.MapGet("/orders/{id:guid}", async (
            Guid id,
            ShopDb db,
            ClaimsPrincipal user) =>
        {
            var customerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(customerId))
                return Results.Unauthorized();

            var order = await db.Orders
                .AsNoTracking()
                .Include(o => o.OrderItems)
                .SingleOrDefaultAsync(o => o.Id == id && o.CustomerId == customerId);

            return order is null ? Results.NotFound() : Results.Ok(order);
        }).RequireAuthorization();

        return app;
    }
}
