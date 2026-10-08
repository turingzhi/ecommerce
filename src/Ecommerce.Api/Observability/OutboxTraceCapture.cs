using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Persistence;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Observability;
public static class OutboxTraceCapture
{
    public static void Capture(ShopDbContext db)
    {
        var activity=Activity.Current;
        if(activity is null || !CommerceTelemetry.TryParseParent(activity.Id,activity.TraceStateString,out _)) return;
        foreach(var entry in db.ChangeTracker.Entries<OutboxMessage>())
        {
            if(entry.State!=EntityState.Added || entry.Entity.TraceParent is not null) continue;
            entry.Entity.TraceParent=activity.Id;
            entry.Entity.TraceState=activity.TraceStateString;
        }
    }
}
