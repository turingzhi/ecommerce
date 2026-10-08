using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Observability;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
namespace Ecommerce.Verification;
public static partial class VerificationRunner
{
    public static async Task RunTelemetry()
    {
        var options=new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(VerificationConnection($"Verify_ecommerce_telemetry_{Guid.NewGuid():N}")).Options;
        try
        {
            await using var db=new ShopDbContext(options);await db.Database.MigrateAsync();
            for(var mode=0;mode<4;mode++)
            {
                using var request=new Activity("origin").SetIdFormat(ActivityIdFormat.W3C).Start();request.TraceStateString="vendor=fixture";
                var message=new OutboxMessage{Type="TraceFixture",Payload="{}"};db.Outbox.Add(message);
                switch(mode){case 0:db.SaveChanges();break;case 1:db.SaveChanges(true);break;case 2:await db.SaveChangesAsync();break;case 3:await db.SaveChangesAsync(true);break;}
                Check(message.TraceParent==request.Id&&message.TraceState=="vendor=fixture","All SQL save overloads capture current origin");
                var origin=message.TraceParent;request.Stop();
                using var retry=new Activity("different").SetIdFormat(ActivityIdFormat.W3C).Start();message.AttemptCount++;
                await db.SaveChangesAsync();Check(message.TraceParent==origin,"Retry preserves persisted origin");
            }
            using var listener=new ActivityListener{ShouldListenTo=s=>s.Name==CommerceTelemetry.SourceName,Sample=(ref ActivityCreationOptions<ActivityContext> _) =>ActivitySamplingResult.AllDataAndRecorded};ActivitySource.AddActivityListener(listener);
            var stored=await db.Outbox.AsNoTracking().FirstAsync();
            CommerceTelemetry.TryParseParent(stored.TraceParent,stored.TraceState,out var context);
            using var producer=CommerceTelemetry.Source.StartActivity("outbox.publish",ActivityKind.Producer,context);
            var envelope=new BrokerEvent(stored.Id,stored.OrderId,stored.Type,stored.Payload,producer!.Id,producer.TraceStateString);
            CommerceTelemetry.TryParseParent(envelope.TraceParent,envelope.TraceState,out var published);
            using var consumer=CommerceTelemetry.Source.StartActivity("event.consume",ActivityKind.Consumer,published);
            Check(producer.TraceId==context.TraceId&&consumer!.TraceId==context.TraceId&&consumer.ParentSpanId==producer.SpanId,"Delayed producer and consumer retain durable request trace");
            Console.WriteLine("Telemetry SQL verification passed");
        }
        finally{await DeleteVerificationDatabase(options);}
    }
}
