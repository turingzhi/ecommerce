using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecommerce.Infrastructure.Persistence;

public sealed class ShopDbContextFactory
    : IDesignTimeDbContextFactory<ShopDbContext>
{
    public ShopDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ECOMMERCE_SQLSERVER")
            ?? throw new InvalidOperationException(
                "ECOMMERCE_SQLSERVER is not configured.");

        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ShopDbContext(options);
    }
}