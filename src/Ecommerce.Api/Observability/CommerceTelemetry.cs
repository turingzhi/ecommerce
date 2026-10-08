using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace Ecommerce.Observability;
public static class CommerceTelemetry
{
    static CommerceTelemetry(){Activity.DefaultIdFormat=ActivityIdFormat.W3C;Activity.ForceDefaultIdFormat=true;}
    public static Activity? StartMessaging(string name,ActivityKind kind,string? parent,string? state)
    {
        return TryParseParent(parent,state,out var context)
            ?Source.StartActivity(name,kind,context)
            :Source.StartActivity(name,kind,parentId:"");
    }
    public static bool TryParseParent(string? parent,string? state,out ActivityContext context)
    {
        context=default;
        if(parent is null || parent.Length!=55 || !parent.StartsWith("00-",StringComparison.Ordinal) || state?.Length>512) return false;
        return ActivityContext.TryParse(parent,state,isRemote:true,out context);
    }
    public const string SourceName="Ecommerce.Commerce";
    public const string MeterName="Ecommerce.Commerce";
    public static readonly ActivitySource Source=new(SourceName,"1.0");
    private static readonly Meter Meter=new(MeterName,"1.0");
    private static readonly Counter<long> Cache=Meter.CreateCounter<long>("ecommerce.search.cache.requests");
    private static readonly Counter<long> Outcomes=Meter.CreateCounter<long>("ecommerce.operation.outcomes");
    private static readonly Histogram<double> Duration=Meter.CreateHistogram<double>("ecommerce.operation.duration","s");
    private static readonly Counter<long> Events=Meter.CreateCounter<long>("ecommerce.messaging.outcomes");
    private static readonly HashSet<string> Operations=["search","cart.read","cart.write","checkout","payment.create","payment.outcome","refund.create","refund.outcome","return.create","return.update","catalog.create","catalog.update","outbox.publish","event.consume"];
    private static readonly HashSet<string> Results=["success","replayed","conflict","invalid","not_found","unavailable","cancelled","failure","handled","duplicate","retry","dead_letter"];
    private static readonly HashSet<string> Dependencies=["sqlserver","redis","elasticsearch","rabbitmq","internal"];
    private static string Operation(string value)=>Operations.Contains(value)?value:"other";
    private static string Result(string value)=>Results.Contains(value)?value:"other";
    public static TelemetryOperation StartOperation(string operation,string dependency)=>new(Operation(operation),Dependencies.Contains(dependency)?dependency:"other");
    public static void RecordCache(string result)=>Cache.Add(1,new KeyValuePair<string,object?>("result",result is "hit" or "miss" or "error"?result:"other"));
    public static void RecordOutcome(string operation,string result)=>Outcomes.Add(1,new("operation",Operation(operation)),new("result",Result(result)));
    public static void RecordEvent(string operation,string type,string result)
    {
        var known=type is "OrderCreated" or "OrderPaid" or "PaymentFailed" or "PaymentUnknown" or "RefundSucceeded" or "RefundFailed" or "RefundUnknown" or "ProductUpserted"?type:"other";
        Events.Add(1,new("operation",Operation(operation)),new("event.type",known),new("result",Result(result)));
    }
    public static async Task<T> MeasureAsync<T>(string operation,string dependency,Func<Task<T>> action,Func<T,string> outcome)
    {
        using var measurement=StartOperation(operation,dependency);
        try {var value=await action();measurement.Complete(outcome(value));return value;}
        catch(OperationCanceledException){measurement.Complete("cancelled");throw;}
        catch(ArgumentException){measurement.Complete("invalid");throw;}
        catch(Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException){measurement.Complete("conflict");throw;}
        catch(KeyNotFoundException){measurement.Complete("not_found");throw;}
    }
    internal static void Finish(string operation,string dependency,string result,double seconds)
    {
        RecordOutcome(operation,result);Duration.Record(seconds,new("operation",operation),new("dependency",dependency),new("result",Result(result)));
    }
}
public sealed class TelemetryOperation:IDisposable
{
    private readonly string operation,dependency;private readonly long start=Stopwatch.GetTimestamp();private readonly Activity? activity;private bool completed;
    internal TelemetryOperation(string operation,string dependency)
    {
        this.operation=operation;this.dependency=dependency;activity=CommerceTelemetry.Source.StartActivity(operation);
        activity?.SetTag("commerce.operation",operation).SetTag("commerce.dependency",dependency);
    }
    public void Complete(string result)
    {
        if(completed)return;completed=true;
        CommerceTelemetry.Finish(operation,dependency,result,Stopwatch.GetElapsedTime(start).TotalSeconds);
        activity?.SetTag("commerce.result",result is "success" or "replayed" or "conflict" or "invalid" or "not_found" or "unavailable" or "cancelled" or "failure"?result:"other");
        if(result is "failure" or "unavailable")activity?.SetStatus(ActivityStatusCode.Error);
        activity?.Dispose();
    }
    public void Dispose(){if(!completed)Complete("failure");}
}
