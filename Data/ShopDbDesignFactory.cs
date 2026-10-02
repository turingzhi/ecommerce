using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecommerce;

public sealed class ShopDbDesignFactory : IDesignTimeDbContextFactory<ShopDb>
{
    public ShopDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ShopDb>()
            .UseSqlite("Data Source=design-time-ecommerce.db")
            .Options;

        return new ShopDb(options);
    }
}
