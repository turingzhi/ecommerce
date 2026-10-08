using Ecommerce.Features.Payments.Models;

namespace Ecommerce.Features.Payments.Contracts;

public record AdminPaymentResponse(Guid Id, Guid OrderId, string CustomerId,
    long AmountCents, string Currency, string Status, DateTime CreatedAt)
{
    public static AdminPaymentResponse From(Payment payment, string customerId) =>
        new(payment.Id, payment.OrderId, customerId, payment.AmountCents,
            payment.Currency, payment.Status,
            DateTime.SpecifyKind(payment.CreatedAt, DateTimeKind.Utc));
}

public record AdminPaymentListResponse(int Page, int PageSize, long Total,
    IReadOnlyList<AdminPaymentResponse> Payments);
