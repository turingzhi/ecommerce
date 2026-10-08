using Ecommerce.Features.Shipments.Contracts;
using System.Text.Json;
using Xunit;
namespace Ecommerce.Api.Tests.Shipments;
public class TrackingResponseTests
{
    [Fact]
    public void CustomerTimelineHasUtcTimesAndNoActorInformation()
    {
        var dto=new OrderTrackingResponse(Guid.NewGuid(),"Paid",null,[new("Pending","Shipped",new DateTime(2026,10,8,1,0,0,DateTimeKind.Utc))]);
        var json=JsonSerializer.Serialize(dto,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("2026-10-08T01:00:00Z",json);
        Assert.DoesNotContain("actorId",json);
    }
}
