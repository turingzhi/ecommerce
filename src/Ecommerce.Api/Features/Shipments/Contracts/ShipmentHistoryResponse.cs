using Ecommerce.Features.Shipments.Models;
namespace Ecommerce.Features.Shipments.Contracts;
public record ShipmentHistoryEntryResponse(long Id,string? FromStatus,string ToStatus,DateTime OccurredAt,string? ActorId)
{
    public static ShipmentHistoryEntryResponse From(ShipmentHistory h)=>new(h.Id,h.FromStatus,h.ToStatus,DateTime.SpecifyKind(h.OccurredAt,DateTimeKind.Utc),h.ActorId);
}
public record ShipmentHistoryResponse(Guid ShipmentId,IReadOnlyList<ShipmentHistoryEntryResponse> History);
