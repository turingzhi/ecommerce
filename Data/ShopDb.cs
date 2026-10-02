using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce;

public class ShopDb(DbContextOptions<ShopDb> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<Order>()
            .HasIndex(o => new
            {
                o.CustomerId,
                o.IdempotencyKey
            })
            .IsUnique();
        
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

        model.Entity<ProcessedMessage>()
            .HasKey(message => message.MessageId);

        model.Entity<OutboxMessage>()
            .HasOne<Order>()
            .WithMany()
            .HasForeignKey(m => m.OrderId);
        model.Entity<Product>()
            .ToTable(t => t.HasCheckConstraint("CK_Stock", "Available >= 0"));
    }
}
