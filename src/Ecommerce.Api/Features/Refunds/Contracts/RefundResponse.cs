using Ecommerce.Features.Refunds.Models;
namespace Ecommerce.Features.Refunds.Contracts;

public record RefundResponse(
    Guid Id, Guid PaymentId, long AmountCents, string Currency,
    string Status, DateTime CreatedAt)
{
    public static RefundResponse From(Refund refund) => new(
        refund.Id, refund.PaymentId, refund.AmountCents,
        refund.Currency, refund.Status, DateTime.SpecifyKind(refund.CreatedAt, DateTimeKind.Utc));
}
