using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Returns.Models;
namespace Ecommerce.Features.Returns.Contracts;
public record ReturnResponse(Guid Id,Guid OrderId,Guid PaymentId,string Reason,string Status,DateTime CreatedAt,
    DateTime? ApprovedAt,DateTime? ReceivedAt,DateTime? CompletedAt,string Currency,long OriginalAmountCents,
    long RefundedCents,long ReservedRefundCents,long RemainingRefundableCents)
{
    public static ReturnResponse From(ReturnRequest value,Payment payment,long refunded,long reserved)=>new(
        value.Id,value.OrderId,value.PaymentId,value.Reason,value.Status,Utc(value.CreatedAt)!.Value,
        Utc(value.ApprovedAt),Utc(value.ReceivedAt),Utc(value.CompletedAt),payment.Currency,payment.AmountCents,
        refunded,reserved,payment.AmountCents-refunded-reserved);
    private static DateTime? Utc(DateTime? value)=>value is null?null:DateTime.SpecifyKind(value.Value,DateTimeKind.Utc);
}
public record ReturnListResponse(int Page,int PageSize,IReadOnlyList<ReturnResponse> Returns);
