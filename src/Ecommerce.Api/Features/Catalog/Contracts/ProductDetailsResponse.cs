using Ecommerce.Features.Catalog.Models;
namespace Ecommerce.Features.Catalog.Contracts;
public record ProductDetailsResponse(int Id,string Name,string Description,string Category,long PriceCents,int Available,string Currency)
{
    public static ProductDetailsResponse From(Product product)=>new(product.Id,product.Name,product.Description,product.Category,product.PriceCents,product.Available,"EUR");
}
