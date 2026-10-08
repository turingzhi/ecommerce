using Ecommerce.Features.Catalog.Models;
namespace Ecommerce.Features.Catalog.Contracts;
public record AdminProductResponse(int Id,string Name,string Description,string Category,long PriceCents,int Available,string Currency,long Version)
{
    public static AdminProductResponse From(Product p)=>new(p.Id,p.Name,p.Description,p.Category,p.PriceCents,p.Available,"EUR",p.Version);
}
public record AdminProductListResponse(IReadOnlyList<AdminProductResponse> Products,int Page,int PageSize,long Total);
