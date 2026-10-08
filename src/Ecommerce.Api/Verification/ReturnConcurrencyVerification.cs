using Ecommerce.Features.Refunds.Services;
using Ecommerce.Features.Returns.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    private static async Task VerifyReturnFilteredListRaceAsync(DbContextOptions<ShopDbContext> options)
    {
        Guid id;
        await using(var db=new ShopDbContext(options)) id=await db.ReturnRequests.Where(r=>r.Status=="Requested").OrderByDescending(r=>r.CreatedAt).ThenByDescending(r=>r.Id).Select(r=>r.Id).FirstAsync();
        var gate=new ReturnListReadGate();
        var gatedOptions=new DbContextOptionsBuilder<ShopDbContext>(options).AddInterceptors(gate).Options;
        var listing=Task.Run(async()=>{await using var db=new ShopDbContext(gatedOptions);return await new ReturnQueryService(db).ListAsync("Requested",1,20,default);});
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using(var db=new ShopDbContext(options))
            {
                var updated=await new ReturnService(db,new RefundService(db)).UpdateStatusAsync(id,new("Approved"),default).WaitAsync(TimeSpan.FromSeconds(10));
                Check(updated.Error is null,"Return advances after filtered list selects identities");
            }
            gate.Release.TrySetResult();
            var result=await listing.WaitAsync(TimeSpan.FromSeconds(10));
            Check(result.Returns.All(r=>r.Status=="Requested")&&!result.Returns.Any(r=>r.Id==id),"Filtered return list excludes rows transitioned after ID selection");
        }
        finally {gate.Release.TrySetResult();await listing.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    private sealed class ReturnListReadGate:DbCommandInterceptor
    {
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData eventData,DbDataReader result,CancellationToken ct=default)
        {
            if(command.CommandText.Contains("SELECT [r].[Id]")&&command.CommandText.Contains("FROM [ReturnRequests]")&&!Entered.Task.IsCompleted)
            {Entered.TrySetResult();await Release.Task.WaitAsync(TimeSpan.FromSeconds(10),ct);}
            return result;
        }
    }
}
