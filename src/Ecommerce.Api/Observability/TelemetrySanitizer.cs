using System.Diagnostics;
namespace Ecommerce.Observability;
public static class TelemetrySanitizer
{
    private static readonly HashSet<string> Allowed=["http.route","http.request.method","http.response.status_code","http.method","http.status_code","network.protocol.version","db.system","db.system.name","db.operation.name","server.port","commerce.operation","commerce.dependency","commerce.result","messaging.system","messaging.operation.type"];
    public static void Apply(Activity activity)
    {
        if(activity.Source.Name.Contains("SqlClient",StringComparison.OrdinalIgnoreCase))activity.DisplayName="sqlserver.query";
        activity.SetStatus(activity.Status);
        foreach(var tag in activity.TagObjects.ToArray())if(!Allowed.Contains(tag.Key))activity.SetTag(tag.Key,null);
    }
}
