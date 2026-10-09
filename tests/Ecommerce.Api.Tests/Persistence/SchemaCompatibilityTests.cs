using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecommerce.Api.Tests.Persistence;

public class SchemaCompatibilityTests
{
    [Fact]
    public void Feature_removal_preserves_the_historical_migration_model()
    {
        using var db = new ShopDbContext(new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer("Server=127.0.0.1;Database=Unused;User Id=unused;Password=unused;TrustServerCertificate=True")
            .Options);
        // This compares models without connecting to SQL Server. Drift would
        // cause startup migration checks to fail or require a schema migration.
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
