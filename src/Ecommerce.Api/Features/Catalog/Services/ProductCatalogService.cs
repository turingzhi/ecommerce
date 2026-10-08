using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Models;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Search;
using Ecommerce.Observability;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Features.Catalog.Services;

public sealed class ProductCatalogService(ShopDbContext db)
{
    private async Task<Product> CreateAsyncMeasuredCore(
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

    public Task<Product> UpdateAsync(int productId,ProductDetailsChange change,CancellationToken cancellationToken=default)=>
        UpdateCoreAsync(productId,change,null,cancellationToken);

    public Task<Product> UpdateVersionedAsync(int productId,ProductDetailsChange change,long expectedVersion,CancellationToken cancellationToken=default)=>
        UpdateCoreAsync(productId,change,expectedVersion,cancellationToken);

    private async Task<Product> UpdateCoreAsyncMeasuredCore(int productId,ProductDetailsChange change,long? expectedVersion,CancellationToken cancellationToken)
    {
        Validate(change);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var product = await db.Products.SingleOrDefaultAsync(
            p => p.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException($"Product {productId} not found");
        if(expectedVersion is not null && product.Version!=expectedVersion)
            throw new DbUpdateConcurrencyException("Product changed; reload before saving.");
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
            change.PriceCents < 0 || change.Name.Trim().Length>200 || change.Category.Trim().Length>200 ||
            change.Description is null || change.Description.Trim().Length>4000)
            throw new ArgumentException("Product name/category are required; price must be non-negative.");
    }

    private Task<Product> UpdateCoreAsync(int productId,ProductDetailsChange change,long? expectedVersion,CancellationToken cancellationToken) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("catalog.update","sqlserver",()=>UpdateCoreAsyncMeasuredCore(productId,change,expectedVersion,cancellationToken),_=>"success");
    public Task<Product> CreateAsync(
        ProductChange change,
        CancellationToken cancellationToken = default) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("catalog.create","sqlserver",()=>CreateAsyncMeasuredCore(change,cancellationToken),_=>"success");
}
