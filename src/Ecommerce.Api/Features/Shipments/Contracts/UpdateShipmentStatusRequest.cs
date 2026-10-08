namespace Ecommerce.Features.Shipments.Contracts;

public record UpdateShipmentStatusRequest(string? Status, string? TrackingNumber = null);
