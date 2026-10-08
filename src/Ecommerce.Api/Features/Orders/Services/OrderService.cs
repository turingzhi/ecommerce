using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Observability;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
namespace Ecommerce.Features.Orders.Services;

public record OrderResult(Order? Order, bool Replayed = false, string? Error = null);

public class OrderService(ShopDbContext db)
{
    private async Task<OrderResult> CreateMeasuredCore(string customerId, string key, CreateOrderRequest request)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        // A missing key needs a range lock too: otherwise two requests can both
        // miss it, and the loser reports a stock conflict instead of replaying.
        var ordersForKey = db.Database.IsSqlServer()
            ? db.Orders.FromSqlInterpolated(
                $"SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK, INDEX(IX_Orders_CustomerId_IdempotencyKey)) WHERE CustomerId = {customerId} AND IdempotencyKey = {key}")
            : db.Orders.Where(o => o.CustomerId == customerId && o.IdempotencyKey == key);

        var existing = await ordersForKey.AsNoTracking()
            .Include(order => order.OrderItems)
            .SingleOrDefaultAsync();
        if (existing is not null)
        {
            if (!OrderRules.MatchesRequest(existing.OrderItems, request.Items))
                return new(null, Error: "Idempotency key already used for different order details");
            return new(existing, true, null);
        }

        var order = new Order
        {
            CustomerId = customerId,
            IdempotencyKey = key,
            // ProductId = request.ProductId, Quantity = request.Quantity,
            // UnitPriceCents = product.PriceCents
        };

        var productIds = request.Items
            .Select(item => item.ProductId)
            .ToList();
        var products = await db.Products
            .Where(p => productIds.Contains(p.Id))
            .AsNoTracking()
            .ToListAsync();

        foreach (var item in request.Items)
        {
            var product = products.FirstOrDefault(p => p.Id == item.ProductId);
            if (product is null) {
                return new(null, Error: "There is no matching product");
            }
            var affected = await db.Products
                .Where(p => p.Id == item.ProductId &&
                    p.Available >= item.Quantity)
                .ExecuteUpdateAsync(set => set.SetProperty(
                    p => p.Available,
                    p => p.Available - item.Quantity
                ));

            if (affected == 0)
            {
                return new(null, Error: "Product unavailable or insufficient stock.");
            }
            var orderItem = new OrderItem
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                UnitPriceCents = product.PriceCents,
                OrderId = order.Id
            };
            order.OrderItems.Add(orderItem);
        }

        db.Orders.Add(order);
        db.Outbox.Add(new OutboxMessage
        {
            OrderId = order.Id,
            Payload = JsonSerializer.Serialize(new { OrderId = order.Id, order.CustomerId })
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(order);
        // On any failure (or early return), disposal rolls back uncommitted changes.
    }
    public async Task<OrderResult> Cancel(
        string customerId,
        Guid orderId)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var orders = db.Database.IsSqlServer()
            ? db.Orders.FromSqlInterpolated(
                $"SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {orderId} AND CustomerId = {customerId}")
            : db.Orders.Where(o => o.CustomerId == customerId && o.Id == orderId);

        var order = await orders
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync();

        if (order is null)
        {
            return new(null, false, "Order not found");
        }

        if (order.Status == "Cancelled")
        {
            return new(order, true);
        }

        if (order.Status != "PendingPayment")
        {
            return new(null, false, "Order cannot be cancelled");
        }

        var hasUnresolvedPayment = await db.Payments.AnyAsync(
            p => p.OrderId == orderId &&
            (p.Status == "Pending" || p.Status == "Unknown")
        );

        if (hasUnresolvedPayment)
        {
            return new(null, false,
                "Order has an unresolved payment and cannot be cancelled");
        }
        foreach (var item in order.OrderItems)
        {
            var quantity = checked((int)item.Quantity);

            var affected = await db.Products
                .Where(p => p.Id == item.ProductId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(
                        p => p.Available,
                        p => p.Available + quantity));

            if (affected != 1)
            {
                return new(null, false, "Product not found");
            }
        }

        order.Status = "Cancelled";

        db.Outbox.Add(new OutboxMessage
        {
            OrderId = orderId,
            Type = "OrderCancelled",
            Payload = JsonSerializer.Serialize(new { OrderId = order.Id, order.CustomerId })
        });

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new OrderResult(order);
    }
    public async Task<OrderResult> Expire(
    Guid orderId,
    DateTime nowUtc)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync();

        var orders = db.Database.IsSqlServer()
            ? db.Orders.FromSqlInterpolated(
                $"SELECT * FROM dbo.Orders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {orderId}")
            : db.Orders.Where(o => o.Id == orderId);

        var order = await orders
            .Include(o => o.OrderItems)
            .SingleOrDefaultAsync();

        if (order is null)
        {
            return new(null, false, "Order not found");
        }

        if (order.Status == "Cancelled")
        {
            return new(order, true);
        }

        if (order.Status != "PendingPayment")
        {
            return new(null, false, "Order cannot expire");
        }

        if (nowUtc < order.CreatedAt.AddMinutes(15))
        {
            return new(null, false, "Order has not expired yet");
        }

        // Next: check unresolved payments, restore stock,
        var hasUnresolvedPayment = await db.Payments.AnyAsync(
        p => p.OrderId == orderId &&
        (p.Status == "Pending" || p.Status == "Unknown")
        );

        if (hasUnresolvedPayment)
        {
            return new(null, false,
                "Order has an unresolved payment and cannot expire");
        }
        var items = order.OrderItems;
        foreach (var item in items)
        {
            var quantity = checked((int)item.Quantity);

            var affected = await db.Products
                .Where(p => p.Id == item.ProductId)
                .ExecuteUpdateAsync(
                    update => update
                    .SetProperty(
                        p => p.Available,
                        p => p.Available + quantity
                    )
                );

            if (affected != 1)
            {
                return new(null, false, "Product not found");
            }
            
        }
        order.Status = "Cancelled";
        db.Outbox.Add(new OutboxMessage
        {
            OrderId = order.Id,
            Type = "OrderCancelled",
            Payload = JsonSerializer.Serialize(new
            {
                OrderId = order.Id,
                CustomerId = order.CustomerId,
                Reason = "Expired"
            })
        });
        // record cancellation, save and commit.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(order);
    }

    public Task<OrderResult> Create(string customerId, string key, CreateOrderRequest request) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("checkout","sqlserver",()=>CreateMeasuredCore(customerId,key,request),r=>r.Error is null?(r.Replayed?"replayed":"success"):"conflict");
}
