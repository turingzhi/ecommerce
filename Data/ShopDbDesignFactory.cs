using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecommerce;

public sealed class ShopDbDesignFactory
    : IDesignTimeDbContextFactory<ShopDb>
{
    public ShopDb CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ECOMMERCE_SQLSERVER")
            ?? throw new InvalidOperationException(
                "ECOMMERCE_SQLSERVER is not configured.");

        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlServer(connectionString)
            .Options;

        return new ShopDb(options);
    }
}