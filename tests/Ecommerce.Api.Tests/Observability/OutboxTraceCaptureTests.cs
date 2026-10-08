using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Observability;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
namespace Ecommerce.Api.Tests.Observability;
public class OutboxTraceCaptureTests
{
    [Fact]
    public void LegacyAndMalformedBrokerContextRemainCompatible()
    {
        var legacy=JsonSerializer.Deserialize<BrokerEvent>(JsonSerializer.Serialize(new BrokerEvent(Guid.NewGuid(),null,"OrderCreated","{}")))!;
        Assert.Null(legacy.TraceParent);Assert.Null(legacy.ToOutboxMessage().TraceState);
        Assert.False(CommerceTelemetry.TryParseParent("bad","secret",out _));
        Assert.True(CommerceTelemetry.TryParseParent("00-0123456789abcdef0123456789abcdef-0123456789abcdef-00",null,out var parent));
        Assert.Equal(ActivityTraceFlags.None,parent.TraceFlags);
        Assert.False(CommerceTelemetry.TryParseParent("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",new string('x',513),out _));
    }
    [Fact]
    public void Invalid_message_context_starts_a_root_instead_of_using_an_unrelated_ambient_request()
    {
        using var listener=new System.Diagnostics.ActivityListener
        {
            ShouldListenTo=s=>s.Name==Ecommerce.Observability.CommerceTelemetry.SourceName,
            Sample=(ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)=>System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId=(ref System.Diagnostics.ActivityCreationOptions<string> _)=>System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var unrelated=new System.Diagnostics.Activity("unrelated").Start();
        using var root=Ecommerce.Observability.CommerceTelemetry.StartMessaging("event.consume",System.Diagnostics.ActivityKind.Consumer,"invalid",null);
        Assert.NotNull(root);
        Assert.NotEqual(unrelated.TraceId,root.TraceId);
        Assert.Equal(default,root.ParentSpanId);
    }
}
