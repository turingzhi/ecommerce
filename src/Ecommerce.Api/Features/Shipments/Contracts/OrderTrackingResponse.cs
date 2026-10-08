using Ecommerce.Features.Shipments.Models;
namespace Ecommerce.Features.Shipments.Contracts;
public record CustomerShipmentHistoryEntry(string? FromStatus,string ToStatus,DateTime OccurredAt);
public record OrderTrackingResponse(Guid OrderId,string OrderStatus,ShipmentResponse? Shipment,IReadOnlyList<CustomerShipmentHistoryEntry> History);
