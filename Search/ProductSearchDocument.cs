namespace Ecommerce.Search;

public class ProductSearchDocument
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string Category { get; set; } = "";

    public long PriceCents { get; set; }
    public long Version { get; set; }

    public static ProductSearchDocument FromProduct(Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Description = product.Description,
        Category = product.Category,
        PriceCents = product.PriceCents,
        Version = product.Version
    };
}
