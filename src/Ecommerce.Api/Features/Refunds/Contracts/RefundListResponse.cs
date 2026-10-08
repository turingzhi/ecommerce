namespace Ecommerce.Features.Refunds.Contracts;

public record RefundListResponse(
    int Page, int PageSize, IReadOnlyList<RefundResponse> Refunds);
