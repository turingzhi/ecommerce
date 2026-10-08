using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    public static async Task RunSqlServer()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ECOMMERCE_SQLSERVER");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Set ECOMMERCE_SQLSERVER in this terminal first.");
        }

        // Reuse the server credentials, but select the test database.
        var testConnection = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "EcommerceVerification"
        };

        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(testConnection.ConnectionString)
            .Options;

        // Create the tables using our SQL Server migrations.
        await using (var db = new ShopDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await VerifySqlServerCheckout(options);
        await VerifySqlServerConcurrentCheckoutReplay(options);
        await VerifySqlServerCompetingCheckout(options);
        await VerifySqlServerMultiItemRollback(options);
        await VerifySqlServerOutboxFailureRollback(options);
        await VerifySqlServerPaymentOutcomeRollback(options);
        await VerifySqlServerRefundOutcomeRollback(options);
        await VerifySqlServerPayment(options);
        await VerifySqlServerConcurrentPaymentReplay(options);
        await VerifySqlServerConcurrentPayment(options);
        await VerifySqlServerPaymentCancellationRace(options);
        await VerifySqlServerPaymentExpirationRace(options);
        await VerifySqlServerConcurrentExpiration(options);
        await VerifySqlServerCancellationExpirationRace(options);
        await VerifySqlServerConcurrentRefundCreation(options);
        foreach (var outcome in new[] { "Succeeded", "Failed", "Unknown" })
        {
            await VerifySqlServerConcurrentPaymentOutcome(options, outcome);
            await VerifySqlServerConcurrentRefundOutcome(options, outcome);
        }
        await VerifySqlServerConcurrentPaymentOutcome(options, "Succeeded", "Failed");
        await VerifySqlServerConcurrentRefundOutcome(options, "Succeeded", "Failed");

        Console.WriteLine("SQL Server verification passed.");
    }

    private static async Task VerifySqlServerCheckout(
        DbContextOptions<ShopDbContext> options)
    {
        // Each run gets its own customer and product.
        var customerId = $"sql-test-{Guid.NewGuid():N}";
        const string key = "checkout-001";
        int productId;
        Guid orderId;

        await using (var db = new ShopDbContext(options))
        {
            var product = new Product
            {
                Name = "SQL verification headphones",
                PriceCents = 5000,
                Available = 10
            };

            db.Products.Add(product);
            await db.SaveChangesAsync();

            // SQL Server generates this ID.
            productId = product.Id;
        }

        var request = new CreateOrderRequest(
            [new CreateOrderItemRequest(productId, 2)]);

        // First request: create an order.
        await using (var db = new ShopDbContext(options))
        {
            var result = await new OrderService(db)
                .Create(customerId, key, request);

            Check(result.Error is null && result.Order is not null
                && !result.Replayed,
                "SQL Server creates a new order");

            orderId = result.Order!.Id;
        }

        // Second request: reuse the same key and body.
        await using (var db = new ShopDbContext(options))
        {
            var replay = await new OrderService(db)
                .Create(customerId, key, request);

            Check(replay.Error is null && replay.Replayed
                && replay.Order?.Id == orderId,
                "Same key returns the original order");
        }

        // Read persisted data through a fresh database context.
        await using (var db = new ShopDbContext(options))
        {
            var product = await db.Products.AsNoTracking()
                .SingleAsync(p => p.Id == productId);

            var order = await db.Orders.AsNoTracking()
                .Include(o => o.OrderItems)
                .SingleAsync(o => o.Id == orderId);

            Check(product.Available == 8,
                "Stock is reserved only once");

            Check(order.Status == "PendingPayment",
                "Saved order is awaiting payment");

            Check(order.OrderItems.Count == 1
                && order.OrderItems[0].ProductId == productId
                && order.OrderItems[0].Quantity == 2
                && order.OrderItems[0].UnitPriceCents == 5000,
                "Order stores quantity and purchase price");

            Check(await db.Orders.CountAsync(o =>
                o.CustomerId == customerId && o.IdempotencyKey == key) == 1,
                "Only one order is stored");

            Check(await db.Outbox.CountAsync(m =>
                m.OrderId == orderId && m.Type == "OrderCreated") == 1,
                "Exactly one OrderCreated event is stored");
        }
    }
}
