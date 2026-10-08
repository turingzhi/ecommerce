using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Refunds.Models;
using Ecommerce.Features.Returns.Models;
using Ecommerce.Features.Shipments.Models;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Observability;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Persistence;

public class ShopDbContext(DbContextOptions<ShopDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ShipmentHistory> ShipmentHistory => Set<ShipmentHistory>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Ecommerce.Observability.OutboxTraceCapture.Capture(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Ecommerce.Observability.OutboxTraceCapture.Capture(this);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<OutboxMessage>().Property(m=>m.TraceParent).HasMaxLength(55);
        model.Entity<OutboxMessage>().Property(m=>m.TraceState).HasMaxLength(512);

        model.Entity<Order>()
            .HasIndex(o => new
            {
                o.CustomerId,
                o.IdempotencyKey
            })
            .IsUnique();

        model.Entity<Order>()
            .HasIndex(o => new { o.CustomerId, o.CreatedAt, o.Id })
            .HasDatabaseName("IX_Orders_CustomerId_CreatedAt_Id")
            .IsDescending(false, true, true)
            .IncludeProperties(o => new { o.Status, o.Currency });

        model.Entity<Payment>()
        .HasIndex(payment => new
        {
            payment.OrderId,
            payment.IdempotencyKey
        })
        .IsUnique();

        model.Entity<Refund>()
        .HasIndex(refund => new
        {
            refund.PaymentId,
            refund.IdempotencyKey
        })
        .IsUnique();
        // model.Entity<Order>().HasOne<Product>().WithMany().HasForeignKey(o => o.ProductId);
        model.Entity<Order>()
            .HasMany(o => o.OrderItems)
            .WithOne()
            .HasForeignKey(item => item.OrderId);

        model.Entity<OrderItem>()
            .HasOne<Product>()
            .WithMany()
            .HasForeignKey(item => item.ProductId);

        model.Entity<Payment>()
            .HasOne<Order>()
            .WithMany()
            .HasForeignKey(payment => payment.OrderId);

        model.Entity<Refund>()
        .HasOne<Payment>()
        .WithMany()
        .HasForeignKey(refund => refund.PaymentId);

        model.Entity<ReturnRequest>().HasIndex(r=>r.OrderId).IsUnique();
        model.Entity<ReturnRequest>().Property(r=>r.Key).HasMaxLength(100);
        model.Entity<ReturnRequest>().Property(r=>r.Reason).HasMaxLength(500);
        model.Entity<ReturnRequest>().HasOne<Order>().WithMany().HasForeignKey(r=>r.OrderId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReturnRequest>().HasOne<Payment>().WithMany().HasForeignKey(r=>r.PaymentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ReturnRequest>().HasIndex(r=>new {r.Status,r.CreatedAt,r.Id}).IsDescending(false,true,true);
        model.Entity<ShipmentHistory>().HasOne<Shipment>().WithMany()
            .HasForeignKey(h=>h.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ShipmentHistory>().HasIndex(h=>new {h.ShipmentId,h.Id});
        model.Entity<ShipmentHistory>().Property(h=>h.ActorId).HasMaxLength(450);
        model.Entity<Shipment>().Property(s => s.TrackingNumber).HasMaxLength(100);
        model.Entity<Shipment>().HasIndex(s => s.OrderId).IsUnique();
        model.Entity<Shipment>().HasOne<Order>().WithMany()
            .HasForeignKey(s => s.OrderId).OnDelete(DeleteBehavior.Restrict);

        model.Entity<ProcessedMessage>()
            .HasKey(message => message.MessageId);

        model.Entity<OutboxMessage>()
            .HasOne<Order>()
            .WithMany()
            .HasForeignKey(m => m.OrderId);
        model.Entity<Product>()
            .ToTable(t => t.HasCheckConstraint("CK_Stock", "Available >= 0"));
        model.Entity<Product>()
            .Property(p => p.Version)
            .IsConcurrencyToken();
    }
}
