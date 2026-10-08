using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
namespace Ecommerce.Observability;
public static class TelemetryRegistration
{
    public static IServiceCollection AddCommerceObservability(this IServiceCollection services,IConfiguration configuration)
    {
        Environment.SetEnvironmentVariable("OTEL_DOTNET_EXPERIMENTAL_SQLCLIENT_ENABLE_TRACE_DB_QUERY_PARAMETERS","false");
        Activity.DefaultIdFormat=ActivityIdFormat.W3C;Activity.ForceDefaultIdFormat=true;
        services.Configure<Microsoft.Extensions.Logging.Console.SimpleConsoleFormatterOptions>(o=>o.IncludeScopes=true);
        services.Configure<LoggerFactoryOptions>(o=>o.ActivityTrackingOptions=ActivityTrackingOptions.TraceId|ActivityTrackingOptions.SpanId);
        var address=configuration["Observability:OtlpEndpoint"];
        var export=Uri.TryCreate(address,UriKind.Absolute,out var endpoint)&&endpoint.Scheme is "http" or "https";
        services.AddOpenTelemetry().ConfigureResource(r=>r.AddService("ecommerce"))
            .WithTracing(traces=>
            {
                traces.AddSource(CommerceTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation(o=>{o.RecordException=false;o.Filter=c=>!c.Request.Path.StartsWithSegments("/health")&&!c.Request.Path.StartsWithSegments("/assets");})
                    .AddHttpClientInstrumentation(o=>o.RecordException=false)
                    .AddSqlClientInstrumentation(o=>{o.RecordException=false;o.EnrichWithSqlCommand=(activity,_)=>{activity.DisplayName="sqlserver.query";TelemetrySanitizer.Apply(activity);};})
                    .AddProcessor(new SanitizingProcessor());
                if(export)traces.AddOtlpExporter(o=>{o.Endpoint=endpoint!;o.TimeoutMilliseconds=3000;});
            })
            .WithMetrics(metrics=>
            {
                metrics.AddMeter(CommerceTelemetry.MeterName).AddAspNetCoreInstrumentation().AddRuntimeInstrumentation();
                if(export)metrics.AddOtlpExporter(o=>{o.Endpoint=endpoint!;o.TimeoutMilliseconds=3000;});
            });
        return services;
    }
    private sealed class SanitizingProcessor:BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)=>TelemetrySanitizer.Apply(activity);
    }
}
