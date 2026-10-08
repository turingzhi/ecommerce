namespace Ecommerce.Features.Shipments.Models;
public class ShipmentHistory
{
    public long Id {get;set;}
    public Guid ShipmentId {get;set;}
    public string? FromStatus {get;set;}
    public string ToStatus {get;set;}="Pending";
    public DateTime OccurredAt {get;set;}=DateTime.UtcNow;
    public string? ActorId {get;set;}
}
