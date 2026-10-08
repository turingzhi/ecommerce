namespace Ecommerce.Features.Catalog.Contracts;
public record UpdateProductRequest(string? Name,string? Description,string? Category,long PriceCents,long ExpectedVersion);
