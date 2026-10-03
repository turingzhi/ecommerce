using System.Text.Json;
using Ecommerce.Search;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce;

public sealed record ProductChange(
    string Name,
    string Description,
    string Category,
    long PriceCents,
    int Available);

public sealed record ProductDetailsChange(
    string Name,
    string Description,
    string Category,
    long PriceCents);

public sealed class ProductCatalogService(ShopDb db)
{
    public async Task<Product> CreateAsync(
        ProductChange change,
        CancellationToken cancellationToken = default)
    {
        Validate(change);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var product = new Product
        {
            Name = change.Name.Trim(),
            Description = change.Description.Trim(),
            Category = change.Category.Trim(),
            PriceCents = change.PriceCents,
            Available = change.Available,
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken); // Obtain the SQL-generated product ID.

        AddEvent(product);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return product;
    }

    public async Task<Product> UpdateAsync(
        int productId,
        ProductDetailsChange change,
        CancellationToken cancellationToken = default)
    {
        Validate(change);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var product = await db.Products.SingleOrDefaultAsync(
            p => p.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException($"Product {productId} not found");
        product.Name = change.Name.Trim();
        product.Description = change.Description.Trim();
        product.Category = change.Category.Trim();
        product.PriceCents = change.PriceCents;
        product.Version = Math.Max(product.Version + 1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        AddEvent(product);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return product;
    }

    private void AddEvent(Product product)
    {
        db.Outbox.Add(new OutboxMessage
        {
            Type = "ProductUpserted",
            Payload = JsonSerializer.Serialize(
                ProductSearchDocument.FromProduct(product))
        });
    }

    private static void Validate(ProductChange change)
    {
        Validate(new ProductDetailsChange(
            change.Name, change.Description, change.Category, change.PriceCents));
        if (change.Available < 0)
        {
            throw new ArgumentException("Product stock must be non-negative.");
        }
    }

    private static void Validate(ProductDetailsChange change)
    {
        if (string.IsNullOrWhiteSpace(change.Name) ||
            string.IsNullOrWhiteSpace(change.Category) ||
            change.PriceCents < 0)
            throw new ArgumentException("Product name/category are required; price must be non-negative.");
    }
}
