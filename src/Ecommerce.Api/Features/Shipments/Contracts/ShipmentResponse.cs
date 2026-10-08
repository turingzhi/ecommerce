using Ecommerce.Features.Shipments.Models;
namespace Ecommerce.Features.Shipments.Contracts;

public record ShipmentResponse(Guid Id, Guid OrderId, string Status, DateTime CreatedAt,
    string? TrackingNumber, DateTime? ShippedAt, DateTime? DeliveredAt)
{
    public static ShipmentResponse From(Shipment shipment) => new(
        shipment.Id, shipment.OrderId, shipment.Status,
        DateTime.SpecifyKind(shipment.CreatedAt, DateTimeKind.Utc), shipment.TrackingNumber,
        AsUtc(shipment.ShippedAt), AsUtc(shipment.DeliveredAt));

    private static DateTime? AsUtc(DateTime? timestamp) => timestamp is { } value
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
}
