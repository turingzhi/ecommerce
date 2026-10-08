using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static async Task VerifySqlServerMultiItemRollback(
        DbContextOptions<ShopDbContext> options)
    {
        var customerId = $"sql-rollback-{Guid.NewGuid():N}";
        int availableProductId;
        int unavailableProductId;

        await using (var db = new ShopDbContext(options))
        {
            var available = new Product
            {
                Name = "Rollback available product",
                PriceCents = 5000,
                Available = 2
            };
            var unavailable = new Product
            {
                Name = "Rollback unavailable product",
                PriceCents = 2000,
                Available = 0
            };
            db.Products.AddRange(available, unavailable);
            await db.SaveChangesAsync();
            availableProductId = available.Id;
            unavailableProductId = unavailable.Id;
        }

        await using (var db = new ShopDbContext(options))
        {
            var result = await new OrderService(db).Create(
                customerId,
                "checkout-001",
                new CreateOrderRequest([
                    new CreateOrderItemRequest(availableProductId, 1),
                    new CreateOrderItemRequest(unavailableProductId, 1)
                ]));
            Check(result.Order is null && result.Error is not null,
                "SQL Server rejects a multi-item order when a later item is unavailable");
        }

        await using (var db = new ShopDbContext(options))
        {
            var availableStock = await db.Products.AsNoTracking()
                .Where(p => p.Id == availableProductId)
                .Select(p => p.Available)
                .SingleAsync();
            var unavailableStock = await db.Products.AsNoTracking()
                .Where(p => p.Id == unavailableProductId)
                .Select(p => p.Available)
                .SingleAsync();
            var orders = await db.Orders.CountAsync(o => o.CustomerId == customerId);
            var items = await db.Set<OrderItem>().CountAsync(i =>
                i.ProductId == availableProductId || i.ProductId == unavailableProductId);
            var events = await db.Outbox.CountAsync(m => m.Payload.Contains(customerId));

            Check(availableStock == 2 && unavailableStock == 0
                && orders == 0 && items == 0 && events == 0,
                "SQL Server rolls back the first stock change and stores no partial order");
        }
    }

    private static async Task VerifySqlServerOutboxFailureRollback(
        DbContextOptions<ShopDbContext> options)
    {
        var customerId = $"sql-outbox-rollback-{Guid.NewGuid():N}";
        var constraintName = $"CK_VerifyOutbox_{Guid.NewGuid():N}";
        int productId;

        await using (var db = new ShopDbContext(options))
        {
            var product = new Product
            {
                Name = "Outbox failure rollback product",
                PriceCents = 5000,
                Available = 2
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            productId = product.Id;

            // Only this test customer's event violates the temporary constraint.
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] ADD CONSTRAINT [{constraintName}] " +
                $"CHECK ([Payload] NOT LIKE '%{customerId}%')");
        }

        try
        {
            var storageFailed = false;
            await using (var db = new ShopDbContext(options))
            {
                try
                {
                    await new OrderService(db).Create(
                        customerId,
                        "checkout-001",
                        new CreateOrderRequest([new CreateOrderItemRequest(productId, 1)]));
                }
                catch (DbUpdateException ex) when (
                    ex.InnerException is SqlException sql
                    && sql.Number == 547
                    && sql.Message.Contains(constraintName, StringComparison.Ordinal))
                {
                    storageFailed = true;
                }
            }
            Check(storageFailed,
                "SQL Server Outbox storage failure reaches the caller");

            await using (var db = new ShopDbContext(options))
            {
                var stock = await db.Products.AsNoTracking()
                    .Where(p => p.Id == productId)
                    .Select(p => p.Available)
                    .SingleAsync();
                var orders = await db.Orders.CountAsync(o => o.CustomerId == customerId);
                var items = await db.Set<OrderItem>()
                    .CountAsync(i => i.ProductId == productId);
                var events = await db.Outbox
                    .CountAsync(m => m.Payload.Contains(customerId));

                Check(stock == 2 && orders == 0 && items == 0 && events == 0,
                    "SQL Server Outbox failure rolls back stock, order, items, and event");
            }
        }
        finally
        {
            await using var db = new ShopDbContext(options);
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [{constraintName}]");
        }
    }

    private static async Task VerifySqlServerPaymentOutcomeRollback(
        DbContextOptions<ShopDbContext> options)
    {
        var customerId = $"sql-payment-rollback-{Guid.NewGuid():N}";
        var constraintName = $"CK_VerifyOutbox_{Guid.NewGuid():N}";
        Guid orderId;
        Guid paymentId;

        await using (var db = new ShopDbContext(options))
        {
            var product = new Product
            {
                Name = "Payment outcome rollback product",
                PriceCents = 5000,
                Available = 1
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();

            var order = await new OrderService(db).Create(
                customerId, "checkout-001",
                new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
            Check(order.Order is not null && order.Error is null,
                "Payment rollback test creates an order");
            orderId = order.Order!.Id;

            var payment = await new PaymentService(db).Create(
                customerId, orderId, "payment-001");
            Check(payment.Payment is not null && payment.Error is null,
                "Payment rollback test creates a payment");
            paymentId = payment.Payment!.Id;

            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] ADD CONSTRAINT [{constraintName}] " +
                $"CHECK ([Payload] NOT LIKE '%{paymentId}%')");
        }

        try
        {
            var storageFailed = false;
            await using (var db = new ShopDbContext(options))
            {
                try { await new PaymentService(db).SimulateSuccess(paymentId); }
                catch (DbUpdateException ex) when (
                    ex.InnerException is SqlException sql
                    && sql.Number == 547
                    && sql.Message.Contains(constraintName, StringComparison.Ordinal))
                {
                    storageFailed = true;
                }
            }
            Check(storageFailed,
                "SQL Server payment outcome exposes Outbox storage failure");

            await using (var db = new ShopDbContext(options))
            {
                var paymentStatus = await db.Payments.AsNoTracking()
                    .Where(p => p.Id == paymentId)
                    .Select(p => p.Status)
                    .SingleAsync();
                var orderStatus = await db.Orders.AsNoTracking()
                    .Where(o => o.Id == orderId)
                    .Select(o => o.Status)
                    .SingleAsync();
                var events = await db.Outbox.CountAsync(m =>
                    m.OrderId == orderId && m.Type == "OrderPaid");
                Check(paymentStatus == "Pending" && orderStatus == "PendingPayment"
                    && events == 0,
                    "SQL Server payment outcome failure rolls back status and event");
            }
        }
        finally
        {
            await using var db = new ShopDbContext(options);
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [{constraintName}]");
        }

        await using (var db = new ShopDbContext(options))
        {
            var retry = await new PaymentService(db).SimulateSuccess(paymentId);
            Check(retry.Payment?.Status == "Succeeded" && retry.Error is null,
                "SQL Server payment outcome succeeds after storage recovers");
        }
    }

    private static async Task VerifySqlServerRefundOutcomeRollback(
        DbContextOptions<ShopDbContext> options)
    {
        var customerId = $"sql-refund-rollback-{Guid.NewGuid():N}";
        var constraintName = $"CK_VerifyOutbox_{Guid.NewGuid():N}";
        Guid orderId;
        Guid refundId;

        await using (var db = new ShopDbContext(options))
        {
            var product = new Product
            {
                Name = "Refund outcome rollback product",
                PriceCents = 5000,
                Available = 1
            };
            db.Products.Add(product);
            await db.SaveChangesAsync();

            var order = await new OrderService(db).Create(
                customerId, "checkout-001",
                new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
            Check(order.Order is not null && order.Error is null,
                "Refund rollback test creates an order");
            orderId = order.Order!.Id;

            var payment = await new PaymentService(db).Create(
                customerId, orderId, "payment-001");
            Check(payment.Payment is not null && payment.Error is null,
                "Refund rollback test creates a payment");
            var paid = await new PaymentService(db)
                .SimulateSuccess(payment.Payment!.Id);
            Check(paid.Payment?.Status == "Succeeded" && paid.Error is null,
                "Refund rollback test pays the order");

            var refund = await new RefundService(db).Create(
                payment.Payment.Id, 3000, "refund-001");
            Check(refund.Refund is not null && refund.Error is null,
                "Refund rollback test creates a refund");
            refundId = refund.Refund!.Id;

            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] ADD CONSTRAINT [{constraintName}] " +
                $"CHECK ([Payload] NOT LIKE '%{refundId}%')");
        }

        try
        {
            var storageFailed = false;
            await using (var db = new ShopDbContext(options))
            {
                try { await new RefundService(db).SimulateSuccess(refundId); }
                catch (DbUpdateException ex) when (
                    ex.InnerException is SqlException sql
                    && sql.Number == 547
                    && sql.Message.Contains(constraintName, StringComparison.Ordinal))
                {
                    storageFailed = true;
                }
            }
            Check(storageFailed,
                "SQL Server refund outcome exposes Outbox storage failure");

            await using (var db = new ShopDbContext(options))
            {
                var refundStatus = await db.Refunds.AsNoTracking()
                    .Where(r => r.Id == refundId)
                    .Select(r => r.Status)
                    .SingleAsync();
                var events = await db.Outbox.CountAsync(m =>
                    m.OrderId == orderId && m.Type == "RefundSucceeded");
                Check(refundStatus == "Pending" && events == 0,
                    "SQL Server refund outcome failure rolls back status and event");
            }
        }
        finally
        {
            await using var db = new ShopDbContext(options);
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[Outbox] DROP CONSTRAINT [{constraintName}]");
        }

        await using (var db = new ShopDbContext(options))
        {
            var retry = await new RefundService(db).SimulateSuccess(refundId);
            Check(retry.Refund?.Status == "Succeeded" && retry.Error is null,
                "SQL Server refund outcome succeeds after storage recovers");
        }
    }
}
