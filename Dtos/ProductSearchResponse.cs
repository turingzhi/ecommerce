using Ecommerce.Search;

namespace Ecommerce.Dtos;

public record ProductSearchResponse(
    int Id,
    string Name,
    string Description,
    string Category,
    long PriceCents)
{
    public static ProductSearchResponse From(ProductSearchDocument document) =>
        new(document.Id, document.Name, document.Description,
            document.Category, document.PriceCents);
}
