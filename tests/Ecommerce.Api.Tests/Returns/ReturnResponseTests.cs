using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Returns.Contracts;
using Ecommerce.Features.Returns.Models;
using Ecommerce.Features.Shipments.Contracts;
using Ecommerce.Features.Shipments.Models;
using System.Text.Json;
using Xunit;
namespace Ecommerce.Api.Tests.Returns;
public class ReturnResponseTests
{
    [Fact]
    public void FinancialTotalsAndNullableUtcTimestampsAreSerialized()
    {
        var response=ReturnResponse.From(new ReturnRequest{CreatedAt=new DateTime(2026,10,8),ApprovedAt=new DateTime(2026,10,8,1,0,0)},new Payment{AmountCents=5000,Currency="EUR"},2000,1000);
        Assert.Equal(2000,response.RemainingRefundableCents);
        var json=JsonSerializer.Serialize(response,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("2026-10-08T00:00:00Z",json);Assert.Contains("2026-10-08T01:00:00Z",json);Assert.Contains("\"completedAt\":null",json);
    }
    [Fact]
    public void ShipmentHistoryKeepsNullableActorAndUtcTime()
    {
        var value=ShipmentHistoryEntryResponse.From(new ShipmentHistory{OccurredAt=new DateTime(2026,10,8)});
        Assert.Null(value.ActorId);Assert.Equal(DateTimeKind.Utc,value.OccurredAt.Kind);
    }
}
