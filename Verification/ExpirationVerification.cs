using Microsoft.EntityFrameworkCore;

using Ecommerce.Dtos;

namespace Ecommerce;

public static partial class Verification
{
    private static async Task VerifyExpirationPaymentSafety()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecommerce-expiration-payments-{Guid.NewGuid()}.db");
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new ShopDb(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 5 });
                await db.SaveChangesAsync();
            }

            Guid orderId;
            DateTime overdueTime;
            await using (var db = new ShopDb(options))
            {
                var created = await new OrderService(db).Create("customer-a", "expiration-safety",
                    new CreateOrder([new CreateOrderItem(1, 2)]));
                Check(created.Error is null && created.Order is not null,
                    "Expiration safety scenario creates a real order");
                orderId = created.Order!.Id;
                overdueTime = created.Order.CreatedAt.AddMinutes(16);
            }

            Guid paymentId;
            await using (var db = new ShopDb(options))
            {
                var created = await new PaymentService(db).Create("customer-a", orderId, "expiration-payment");
                Check(created.Error is null && created.Payment?.Status == "Pending",
                    "Expiration safety scenario creates a Pending payment");
                paymentId = created.Payment!.Id;
            }

            async Task CheckBlocked(string paymentStatus, string orderStatus, int expectedEvents)
            {
                await using (var db = new ShopDb(options))
                {
                    var result = await new OrderService(db).Expire(orderId, overdueTime);
                    var expectedError = orderStatus == "Paid"
                        ? "Order cannot expire"
                        : "Order has an unresolved payment and cannot expire";
                    Check(result.Error == expectedError && result.Order is null && !result.Replayed,
                        $"Overdue order with {paymentStatus} payment cannot expire");
                }
                await using (var db = new ShopDb(options))
                {
                    Check((await db.Products.SingleAsync()).Available == 3,
                        $"Rejected expiration with {paymentStatus} payment keeps stock reserved");
                    Check((await db.Orders.SingleAsync()).Status == orderStatus &&
                          (await db.Payments.SingleAsync()).Status == paymentStatus,
                        "Rejected expiration preserves order and payment statuses");
                    Check(await db.Outbox.CountAsync() == expectedEvents &&
                          await db.Outbox.CountAsync(m => m.Type == "OrderCreated") == 1 &&
                          await db.Outbox.CountAsync(m => m.Type == "OrderPaid") == expectedEvents - 1,
                        "Rejected expiration preserves Outbox without a cancellation event");
                }
            }

            await CheckBlocked("Pending", "PendingPayment", 1);
            await using (var db = new ShopDb(options))
            {
                var timeout = await new PaymentService(db).SimulateTimeout(paymentId);
                Check(timeout.Error is null && timeout.Payment?.Status == "Unknown",
                    "Expiration safety payment becomes Unknown");
            }
            await CheckBlocked("Unknown", "PendingPayment", 1);
            await using (var db = new ShopDb(options))
            {
                var success = await new PaymentService(db).SimulateSuccess(paymentId);
                Check(success.Error is null && success.Payment?.Status == "Succeeded",
                    "Expiration safety payment resolves successfully");
            }
            await CheckBlocked("Succeeded", "Paid", 2);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
