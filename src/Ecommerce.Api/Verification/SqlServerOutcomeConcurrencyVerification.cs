using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Contracts;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static async Task VerifySqlServerConcurrentPaymentOutcome(
        DbContextOptions<ShopDbContext> options,
        string outcome,
        string? competingOutcome = null)
    {
        competingOutcome ??= outcome;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var customerId = $"sql-payment-outcome-{Guid.NewGuid():N}";
            Guid orderId;
            Guid paymentId;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Payment outcome verification product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();

                var order = await new OrderService(db).Create(
                    customerId, "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
                Check(order.Order is not null && order.Error is null,
                    "Payment outcome test creates an order");
                orderId = order.Order!.Id;

                var payment = await new PaymentService(db).Create(
                    customerId, orderId, "payment-001");
                Check(payment.Payment is not null && payment.Error is null,
                    "Payment outcome test creates a payment");
                paymentId = payment.Payment!.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<PaymentResult> ApplyOutcome(string requestedOutcome)
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                var service = new PaymentService(db);
                return requestedOutcome switch
                {
                    "Succeeded" => await service.SimulateSuccess(paymentId),
                    "Failed" => await service.SimulateFailure(paymentId),
                    "Unknown" => await service.SimulateTimeout(paymentId),
                    _ => throw new ArgumentException("Unsupported payment outcome", nameof(requestedOutcome))
                };
            }

            var first = ApplyOutcome(outcome);
            var second = ApplyOutcome(competingOutcome);
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var paymentStatus = await checkDb.Payments.AsNoTracking()
                .Where(p => p.Id == paymentId)
                .Select(p => p.Status)
                .SingleAsync();
            var orderStatus = await checkDb.Orders.AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => o.Status)
                .SingleAsync();
            var outcomeEvents = await checkDb.Outbox
                .Where(m => m.OrderId == orderId && m.Type != "OrderCreated")
                .Select(m => m.Type)
                .ToListAsync();

            if (outcome != competingOutcome)
            {
                var expectedEvent = paymentStatus == "Succeeded"
                    ? "OrderPaid" : "PaymentFailed";
                Check(results.Count(r => r.Error is null && !r.Replayed
                        && r.Payment?.Id == paymentId) == 1
                    && results.Count(r => r.Error is not null && r.Payment is null) == 1
                    && (paymentStatus == "Succeeded" || paymentStatus == "Failed")
                    && orderStatus == (paymentStatus == "Succeeded" ? "Paid" : "PendingPayment")
                    && outcomeEvents.Count == 1 && outcomeEvents[0] == expectedEvent,
                    $"Conflicting payment outcomes have one winner and one event " +
                    $"(attempt {attempt + 1}, status={paymentStatus}, events={outcomeEvents.Count})");
                continue;
            }

            Check(results.All(r => r.Error is null && r.Payment?.Id == paymentId)
                && results.Count(r => r.Replayed) == 1
                && paymentStatus == outcome
                && orderStatus == (outcome == "Succeeded" ? "Paid" : "PendingPayment")
                && outcomeEvents.Count == (outcome == "Unknown" ? 0 : 1),
                $"Concurrent payment {outcome} applies once and replays once " +
                $"(attempt {attempt + 1}, events={outcomeEvents.Count})");
        }
    }

    private static async Task VerifySqlServerConcurrentRefundOutcome(
        DbContextOptions<ShopDbContext> options,
        string outcome,
        string? competingOutcome = null)
    {
        competingOutcome ??= outcome;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var customerId = $"sql-refund-outcome-{Guid.NewGuid():N}";
            Guid orderId;
            Guid refundId;

            await using (var db = new ShopDbContext(options))
            {
                var product = new Product
                {
                    Name = "Refund outcome verification product",
                    PriceCents = 5000,
                    Available = 1
                };
                db.Products.Add(product);
                await db.SaveChangesAsync();

                var order = await new OrderService(db).Create(
                    customerId, "checkout-001",
                    new CreateOrderRequest([new CreateOrderItemRequest(product.Id, 1)]));
                Check(order.Order is not null && order.Error is null,
                    "Refund outcome test creates an order");
                orderId = order.Order!.Id;

                var payment = await new PaymentService(db).Create(
                    customerId, orderId, "payment-001");
                Check(payment.Payment is not null && payment.Error is null,
                    "Refund outcome test creates a payment");
                var paid = await new PaymentService(db)
                    .SimulateSuccess(payment.Payment!.Id);
                Check(paid.Payment?.Status == "Succeeded" && paid.Error is null,
                    "Refund outcome test pays the order");

                var refund = await new RefundService(db).Create(
                    payment.Payment.Id, 3000, "refund-001");
                Check(refund.Refund is not null && refund.Error is null,
                    "Refund outcome test creates a refund");
                refundId = refund.Refund!.Id;
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<RefundResult> ApplyOutcome(string requestedOutcome)
            {
                await start.Task;
                await using var db = new ShopDbContext(options);
                var service = new RefundService(db);
                return requestedOutcome switch
                {
                    "Succeeded" => await service.SimulateSuccess(refundId),
                    "Failed" => await service.SimulateFailure(refundId),
                    "Unknown" => await service.SimulateTimeout(refundId),
                    _ => throw new ArgumentException("Unsupported refund outcome", nameof(requestedOutcome))
                };
            }

            var first = ApplyOutcome(outcome);
            var second = ApplyOutcome(competingOutcome);
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            await using var checkDb = new ShopDbContext(options);
            var refundStatus = await checkDb.Refunds.AsNoTracking()
                .Where(r => r.Id == refundId)
                .Select(r => r.Status)
                .SingleAsync();
            var outcomeEvents = await checkDb.Outbox
                .Where(m => m.OrderId == orderId && m.Type.StartsWith("Refund"))
                .Select(m => m.Type)
                .ToListAsync();

            if (outcome != competingOutcome)
            {
                var expectedEvent = refundStatus == "Succeeded"
                    ? "RefundSucceeded" : "RefundFailed";
                Check(results.Count(r => r.Error is null && !r.Replayed
                        && r.Refund?.Id == refundId) == 1
                    && results.Count(r => r.Error is not null && r.Refund is null) == 1
                    && (refundStatus == "Succeeded" || refundStatus == "Failed")
                    && outcomeEvents.Count == 1 && outcomeEvents[0] == expectedEvent,
                    $"Conflicting refund outcomes have one winner and one event " +
                    $"(attempt {attempt + 1}, status={refundStatus}, events={outcomeEvents.Count})");
                continue;
            }

            Check(results.All(r => r.Error is null && r.Refund?.Id == refundId)
                && results.Count(r => r.Replayed) == 1
                && refundStatus == outcome
                && outcomeEvents.Count == 1,
                $"Concurrent refund {outcome} applies once and replays once " +
                $"(attempt {attempt + 1}, events={outcomeEvents.Count})");
        }
    }
}
