using Ecommerce.Features.Payments.Models;
namespace Ecommerce.Features.Payments.Contracts;

public record PaymentResponse(
    Guid Id,
    Guid OrderId,
    long AmountCents,
    string Currency,
    string Status,
    DateTime CreatedAt)
{
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        payment.OrderId,
        payment.AmountCents,
        payment.Currency,
        payment.Status,
        DateTime.SpecifyKind(payment.CreatedAt, DateTimeKind.Utc));
}
