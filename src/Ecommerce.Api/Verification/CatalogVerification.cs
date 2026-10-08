using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    public static async Task RunCatalog()
    {
        var options=new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(VerificationConnection($"Verify_ecommerce_catalog_{Guid.NewGuid():N}")).Options;
        try
        {
            await using(var db=new ShopDbContext(options)) await db.Database.MigrateAsync();
            await VerifyCatalogReadsAsync(options);
            await VerifyCatalogAdminAsync(options);
            Console.WriteLine("Catalog SQL verification passed");
        }
        finally {await DeleteVerificationDatabase(options);}
    }
    private static async Task VerifyCatalogReadsAsync(DbContextOptions<ShopDbContext> options)
    {
        await using var db=new ShopDbContext(options);
        var products=new[]{new Product{Name="One",Category="Alpha",PriceCents=1000,Available=2},new Product{Name="Two",Category="Alpha",PriceCents=1000},new Product{Name="Three",Category="alpha",PriceCents=3000}};
        db.Products.AddRange(products);await db.SaveChangesAsync();
        var query=new ProductQueryService(db);
        var asc=await query.BrowseAsync(new(null,null,null,1,20,"priceAsc"),default);
        Check(asc.Total==3&&asc.Products.Select(p=>p.Id).SequenceEqual(products.Select(p=>p.Id)),"Browse ascending price ties use ID ascending");
        var desc=await query.BrowseAsync(new(null,null,null,1,20,"priceDesc"),default);
        Check(desc.Products.Select(p=>p.Id).SequenceEqual(new[]{products[2].Id,products[0].Id,products[1].Id}),"Browse descending price preserves tie order");
        var category=await query.BrowseAsync(new("Alpha",null,null,1,20,"idAsc"),default);
        Check(category.Total==2&&category.Products.All(p=>p.Category=="Alpha"),"SQL browse category is exact case sensitive");
        Check((await query.BrowseAsync(new(null,1000,1000,1,20,"idAsc"),default)).Total==2,"Browse inclusive price bounds");
        var first=await query.BrowseAsync(new(null,null,null,1,1,"idAsc"),default);
        var second=await query.BrowseAsync(new(null,null,null,2,1,"idAsc"),default);
        Check(first.Products[0].Id!=second.Products[0].Id&&first.Total==3,"Browse pages disjoint with total");
        Check((await query.BrowseAsync(new(null,null,null,10,1,"idAsc"),default)).Products.Count==0,"Browse empty beyond matches");
        await db.Products.Where(p=>p.Id==products[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.Available,1));
        Check((await query.GetAsync(products[0].Id,default)) is {Available:1,Currency:"EUR"},"SQL details expose current availability");
        Check(await query.GetAsync(-1,default) is null&&await query.GetAsync(int.MaxValue,default) is null,"Missing product detail is absent");
    }
}
