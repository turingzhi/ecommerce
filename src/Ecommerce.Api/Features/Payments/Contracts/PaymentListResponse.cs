namespace Ecommerce.Features.Payments.Contracts;
public record PaymentListResponse(int Page,int PageSize,IReadOnlyList<PaymentResponse> Payments);
