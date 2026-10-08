namespace Ecommerce.Features.Catalog.Contracts;

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
