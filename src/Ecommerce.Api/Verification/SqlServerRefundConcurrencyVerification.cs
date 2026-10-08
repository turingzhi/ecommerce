using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Refunds.Contracts;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static async Task VerifySqlServerConcurrentRefundCreation(
        DbContextOptions<ShopDbContext> options)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var customerId = $"sql-concurrent-refund-{Guid.NewGuid():N}";
            Guid paymentId;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Concurrent refund verification product",
                    PriceCents = 10000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();

                var order = await new OrderService(db).Create(
                    customerId,
                    "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
                Check(order.Order is not null && order.Error is null,
                    "Concurrent refund test creates an order");

                var payment = await new PaymentService(db).Create(
                    customerId, order.Order!.Id, "payment-001");
                Check(payment.Payment is not null && payment.Error is null,
                    "Concurrent refund test creates a payment");
                paymentId = payment.Payment!.Id;

                var succeeded = await new PaymentService(db)
                    .SimulateSuccess(paymentId);
                Check(succeeded.Payment?.Status == "Succeeded" && succeeded.Error is null,
                    "Concurrent refund test pays the order");
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<RefundResult> CreateRefundRequest(string key)
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                return await new RefundService(db).Create(paymentId, 6000, key);
            }

            var first = CreateRefundRequest("refund-a");
            var second = CreateRefundRequest("refund-b");
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var stored = await checkDb.Refunds.AsNoTracking()
                .Where(r => r.PaymentId == paymentId)
                .ToListAsync();

            Check(results.Count(r => r.Refund is not null && r.Error is null) == 1
                && results.Count(r => r.Refund is null && r.Error is not null) == 1
                && stored.Count == 1
                && stored.Sum(r => r.AmountCents) == 6000,
                $"Concurrent refunds cannot reserve 12000 from a 10000 payment " +
                $"(attempt {attempt + 1}, stored={stored.Count}, " +
                $"amount={stored.Sum(r => r.AmountCents)})");
        }
    }
}
