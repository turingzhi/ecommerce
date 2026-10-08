using Ecommerce.Common.Pagination;
using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Features.Catalog.Services;
public class ProductQueryService(ShopDbContext db)
{
    public async Task<ProductDetailsResponse?> GetAsync(int id,CancellationToken ct)
    {
        var product=await db.Products.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct);
        return product is null?null:ProductDetailsResponse.From(product);
    }
    public async Task<ProductPageResponse> BrowseAsync(ProductBrowseParameters parameters,CancellationToken ct)
    {
        var query=db.Products.AsNoTracking();
        if(parameters.Category is not null) query=query.Where(p=>EF.Functions.Collate(p.Category,"Latin1_General_100_BIN2")==parameters.Category);
        if(parameters.MinPriceCents is not null)query=query.Where(p=>p.PriceCents>=parameters.MinPriceCents);
        if(parameters.MaxPriceCents is not null)query=query.Where(p=>p.PriceCents<=parameters.MaxPriceCents);
        var total=await query.LongCountAsync(ct);
        var ordered=parameters.Sort switch
        {
            "priceAsc"=>query.OrderBy(p=>p.PriceCents).ThenBy(p=>p.Id),
            "priceDesc"=>query.OrderByDescending(p=>p.PriceCents).ThenBy(p=>p.Id),
            _=>query.OrderBy(p=>p.Id)
        };
        var products=await ordered.Skip(parameters.Offset).Take(parameters.PageSize)
            .Select(p=>new ProductSummaryResponse(p.Id,p.Name,p.Description,p.Category,p.PriceCents)).ToListAsync(ct);
        return new(products,total,parameters.Page,parameters.PageSize);
    }
    public async Task<AdminProductListResponse> ListForAdminAsync(string? name,string? category,PageBounds bounds,CancellationToken ct)
    {
        var query=db.Products.AsNoTracking();
        if(!string.IsNullOrWhiteSpace(name))query=query.Where(p=>EF.Functions.Collate(p.Name,"Latin1_General_100_CI_AS").Contains(name.Trim()));
        if(!string.IsNullOrWhiteSpace(category))query=query.Where(p=>EF.Functions.Collate(p.Category,"Latin1_General_100_BIN2")==category.Trim());
        var total=await query.LongCountAsync(ct);
        if(bounds.Offset>int.MaxValue)return new([],bounds.Page,bounds.PageSize,total);
        var rows=await query.OrderBy(p=>p.Id).Skip((int)bounds.Offset).Take(bounds.PageSize).ToListAsync(ct);
        return new(rows.Select(AdminProductResponse.From).ToList(),bounds.Page,bounds.PageSize,total);
    }
}
