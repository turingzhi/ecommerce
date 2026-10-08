using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    private static async Task VerifyCatalogAdminAsync(DbContextOptions<ShopDbContext> options)
    {
        int id;long version;
        await using(var db=new ShopDbContext(options))
        {
            var value=await new ProductCatalogService(db).CreateAsync(new(" Edit "," details "," Admin ",5000,2));id=value.Id;version=value.Version;
            Check(value.Name=="Edit"&&value.Description=="details"&&value.Category=="Admin","Catalog trims saved text");
        }
        foreach(var invalid in new[]{new ProductChange(null!,"","Admin",1,1),new ProductChange(new string('n',201),"","Admin",1,1),new ProductChange("name",null!,"Admin",1,1),new ProductChange("name",new string('d',4001),"Admin",1,1),new ProductChange("name","","",1,1),new ProductChange("name","","Admin",-1,1),new ProductChange("name","","Admin",1,-1)})
        {
            await using var db=new ShopDbContext(options);var rejected=false;
            try{await new ProductCatalogService(db).CreateAsync(invalid);}catch(ArgumentException){rejected=true;}
            Check(rejected,"Catalog rejects invalid/null/oversized input");
        }
        await using(var db=new ShopDbContext(options))
        {
            var count=await db.Outbox.CountAsync();var rejected=false;
            try{await new ProductCatalogService(db).UpdateVersionedAsync(id,new("Stale","","Admin",1000),version-1,default);}catch(DbUpdateConcurrencyException){rejected=true;}
            Check(rejected&&await db.Outbox.CountAsync()==count&&(await db.Products.AsNoTracking().SingleAsync(p=>p.Id==id)).Name=="Edit","Stale version saves no product or event");
        }
        async Task<bool> Edit(string name)
        {
            await using var db=new ShopDbContext(options);try{await new ProductCatalogService(db).UpdateVersionedAsync(id,new(name,"","Admin",6000),version,default);return true;}catch(DbUpdateConcurrencyException){return false;}
        }
        int before;await using(var db=new ShopDbContext(options))before=await db.Outbox.CountAsync();
        var outcomes=await Task.WhenAll(Edit("Winner A"),Edit("Winner B")).WaitAsync(TimeSpan.FromSeconds(10));
        await using(var db=new ShopDbContext(options))Check(outcomes.Count(x=>x)==1&&await db.Outbox.CountAsync()==before+1,"Concurrent catalog edits yield one winner and one snapshot");
        // Force the failure after the product SQL write but before committing the Outbox snapshot.
        await using(var db=new ShopDbContext(options))await db.Database.ExecuteSqlRawAsync("ALTER TABLE [Outbox] WITH NOCHECK ADD CONSTRAINT [CK_CatalogVerify] CHECK (1=0)");
        try
        {
            await using var db=new ShopDbContext(options);var failed=false;var current=await db.Products.AsNoTracking().SingleAsync(p=>p.Id==id);
            try{await new ProductCatalogService(db).UpdateVersionedAsync(id,new("Rollback","","Admin",1),current.Version,default);}catch(DbUpdateException){failed=true;}
            await using var read=new ShopDbContext(options);
            Check(failed&&(await read.Products.AsNoTracking().SingleAsync(p=>p.Id==id)).Name==current.Name,"Catalog snapshot failure rolls back details");
        }
        finally{await using var db=new ShopDbContext(options);await db.Database.ExecuteSqlRawAsync("ALTER TABLE [Outbox] DROP CONSTRAINT [CK_CatalogVerify]");}
        var gate=new CatalogEditGate();var gated=new DbContextOptionsBuilder<ShopDbContext>(options).AddInterceptors(gate).Options;
        await using(var db=new ShopDbContext(options))version=await db.Products.Where(p=>p.Id==id).Select(p=>p.Version).SingleAsync();
        var editing=Task.Run(async()=>{await using var db=new ShopDbContext(gated);return await new ProductCatalogService(db).UpdateVersionedAsync(id,new("After checkout","","Admin",7000),version,default);});
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using(var db=new ShopDbContext(options))
            {
                var order=await new OrderService(db).Create("catalog-race","race",new([new(id,1)]));
                Check(order.Error is null&&order.Order!.OrderItems.Single().UnitPriceCents==6000,"Checkout preserves its purchase price during editing");
            }
            gate.Release.TrySetResult();await editing.WaitAsync(TimeSpan.FromSeconds(10));
            await using var read=new ShopDbContext(options);
            Check((await read.Products.SingleAsync(p=>p.Id==id)) is {Available:1,PriceCents:7000},"Admin detail save cannot restore reserved stock");
        }
        finally{gate.Release.TrySetResult();await editing.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    private sealed class CatalogEditGate:Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData data,System.Data.Common.DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("FROM [Products]")&&!Entered.Task.IsCompleted){Entered.TrySetResult();await Release.Task.WaitAsync(TimeSpan.FromSeconds(10),ct);}return result;
        }
    }
}
