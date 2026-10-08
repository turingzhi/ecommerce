using Ecommerce.Features.Shipments.Contracts;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Features.Shipments.Services;

public enum ShipmentUpdateError { InvalidRequest, NotFound, Conflict }

public record ShipmentUpdateResult(Shipment? Shipment, ShipmentUpdateError? Error = null,
    string? Detail = null, bool Replayed = false);

public class ShipmentService(ShopDbContext db)
{
    public async Task<ShipmentUpdateResult> UpdateStatusAsync(Guid shipmentId,
        UpdateShipmentStatusRequest request, string actorId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actorId) || actorId.Length>450)
            return new(null,ShipmentUpdateError.InvalidRequest,"Admin identity is required.");
        if (request.Status is not ("Pending" or "Shipped" or "Delivered"))
            return new(null, ShipmentUpdateError.InvalidRequest, "Status must be Shipped or Delivered.");
        var tracking = request.TrackingNumber?.Trim();
        if ((request.Status == "Shipped" || tracking is not null) &&
            (string.IsNullOrEmpty(tracking) || tracking.Length > 100 || tracking.Any(char.IsControl)))
            return new(null, ShipmentUpdateError.InvalidRequest, "Tracking number must contain 1–100 characters without control characters.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var shipment = await db.Shipments.FromSqlInterpolated(
            $"SELECT * FROM [Shipments] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {shipmentId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (shipment is null) return new(null, ShipmentUpdateError.NotFound, "Shipment not found.");
        var previousStatus=shipment.Status;
        if (request.Status == "Pending")
            return new(null, ShipmentUpdateError.Conflict, "Shipment cannot be reset to Pending.");
        if (request.Status == "Shipped")
        {
            if (shipment.Status == "Shipped" && shipment.TrackingNumber == tracking)
                return new(shipment, Replayed: true);
            if (shipment.Status != "Pending")
                return new(null, ShipmentUpdateError.Conflict, "Shipment cannot move to Shipped or change tracking.");
            shipment.Status = "Shipped";
            shipment.TrackingNumber = tracking;
            shipment.ShippedAt = DateTime.UtcNow;
        }
        else
        {
            if (tracking is not null && tracking != shipment.TrackingNumber)
                return new(null, ShipmentUpdateError.Conflict, "Tracking number cannot change.");
            if (shipment.Status == "Delivered") return new(shipment, Replayed: true);
            if (shipment.Status != "Shipped")
                return new(null, ShipmentUpdateError.Conflict, "Only a Shipped shipment can become Delivered.");
            shipment.Status = "Delivered";
            shipment.DeliveredAt = DateTime.UtcNow;
        }
        db.ShipmentHistory.Add(new ShipmentHistory {ShipmentId=shipment.Id,FromStatus=previousStatus,
            ToStatus=shipment.Status,ActorId=actorId,OccurredAt=(shipment.Status=="Shipped"?shipment.ShippedAt:shipment.DeliveredAt)!.Value});
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(shipment);
    }
}
