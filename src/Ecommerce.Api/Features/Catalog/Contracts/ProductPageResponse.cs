namespace Ecommerce.Features.Catalog.Contracts;

public record ProductPageResponse(
    IReadOnlyList<ProductSummaryResponse> Products,
    long Total,
    int Page,
    int PageSize);
