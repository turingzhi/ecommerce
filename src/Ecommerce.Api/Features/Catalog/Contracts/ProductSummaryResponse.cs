using Ecommerce.Infrastructure.Search;
namespace Ecommerce.Features.Catalog.Contracts;

public record ProductSummaryResponse(
    int Id,
    string Name,
    string Description,
    string Category,
    long PriceCents)
{
    public static ProductSummaryResponse From(ProductSearchDocument document) =>
        new(document.Id, document.Name, document.Description,
            document.Category, document.PriceCents);
}
