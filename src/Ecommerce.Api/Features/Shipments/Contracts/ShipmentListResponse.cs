namespace Ecommerce.Features.Shipments.Contracts;
public record ShipmentListResponse(int Page,int PageSize,IReadOnlyList<ShipmentResponse> Shipments);
