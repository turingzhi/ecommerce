using Ecommerce.Features.Shipments.Contracts;
using Ecommerce.Features.Shipments.Models;
using System.Text.Json;
using Xunit;

namespace Ecommerce.Api.Tests.Shipments;

public class ShipmentResponseTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TransitionTimestampsRemainUtcAfterSqlRoundTrip(DateTimeKind kind)
    {
        var shipment = new Shipment { TrackingNumber = "TRACK-001",
            ShippedAt = new DateTime(2026, 10, 8, 1, 0, 0, kind),
            DeliveredAt = new DateTime(2026, 10, 8, 2, 0, 0, kind) };
        var response = JsonSerializer.SerializeToElement(ShipmentResponse.From(shipment));
        Assert.Equal("TRACK-001", response.GetProperty("TrackingNumber").GetString());
        Assert.Equal("2026-10-08T01:00:00Z", response.GetProperty("ShippedAt").GetString());
        Assert.Equal("2026-10-08T02:00:00Z", response.GetProperty("DeliveredAt").GetString());
    }

    [Fact]
    public void PendingShipmentHasNullTransitionDetails()
    {
        var response = JsonSerializer.SerializeToElement(ShipmentResponse.From(new Shipment()));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("TrackingNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("ShippedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("DeliveredAt").ValueKind);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ShipmentTimestampRemainsUtcAfterSqlRoundTrip(DateTimeKind kind)
    {
        var shipment = new Shipment { CreatedAt = new DateTime(2026, 10, 8, 0, 0, 0, kind) };
        var response = ShipmentResponse.From(shipment);
        Assert.Equal("2026-10-08T00:00:00Z",
            JsonSerializer.SerializeToElement(response).GetProperty("CreatedAt").GetString());
    }
}
