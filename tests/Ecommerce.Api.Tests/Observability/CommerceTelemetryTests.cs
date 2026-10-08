using Ecommerce.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;
namespace Ecommerce.Api.Tests.Observability;
public class CommerceTelemetryTests
{
    [Fact]
    public void MetricsAreBoundedAndOperationCompletesOnce()
    {
        var cache=0L;var outcomes=0L;var duration=0;
        var labels=new List<string>();
        using var listener=new MeterListener();
        listener.InstrumentPublished=(instrument,l)=>{if(instrument.Meter.Name==CommerceTelemetry.MeterName)l.EnableMeasurementEvents(instrument);};
        listener.SetMeasurementEventCallback<long>((instrument,value,tags,_)=>{if(instrument.Name=="ecommerce.search.cache.requests")cache+=value;if(instrument.Name=="ecommerce.operation.outcomes")outcomes+=value;foreach(var tag in tags)labels.Add(tag.Value?.ToString()??"");});
        listener.SetMeasurementEventCallback<double>((instrument,value,tags,_)=>{if(instrument.Name=="ecommerce.operation.duration")duration++;});listener.Start();
        CommerceTelemetry.RecordCache("hit");CommerceTelemetry.RecordCache("miss");CommerceTelemetry.RecordCache("error");
        using(var operation=CommerceTelemetry.StartOperation("user-private-id","redis-key-secret")){operation.Complete("query-secret");operation.Complete("success");}
        Assert.Equal(3,cache);Assert.Equal(1,outcomes);Assert.Equal(1,duration);
        Assert.DoesNotContain("user-private-id",labels);Assert.DoesNotContain("redis-key-secret",labels);Assert.DoesNotContain("query-secret",labels);Assert.Contains("other",labels);
    }
    [Fact]
    public void SanitizerRemovesSensitiveAutomaticAttributes()
    {
        using var activity=new Activity("request");activity.SetTag("url.full","http://x/?q=secret").SetTag("db.query.text","SELECT secret").SetTag("http.request.header.authorization","Bearer secret").SetTag("http.route","/products/{id}").SetTag("http.request.method","GET");
        TelemetrySanitizer.Apply(activity);
        Assert.Null(activity.GetTagItem("url.full"));Assert.Null(activity.GetTagItem("db.query.text"));Assert.Null(activity.GetTagItem("http.request.header.authorization"));Assert.Equal("/products/{id}",activity.GetTagItem("http.route"));
    }
}
