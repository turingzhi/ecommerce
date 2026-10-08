namespace Ecommerce.Features.Catalog.Contracts;
public record CreateProductRequest(string? Name,string? Description,string? Category,long PriceCents,int Available);
