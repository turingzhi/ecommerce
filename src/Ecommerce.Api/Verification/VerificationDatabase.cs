using Ecommerce.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    private static string VerificationConnection(string path)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ECOMMERCE_SQLSERVER");
        if (string.IsNullOrWhiteSpace(baseConnection))
            throw new InvalidOperationException("Set ECOMMERCE_SQLSERVER before running verification.");

        var builder = new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = path,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static async Task DeleteVerificationDatabase(
        DbContextOptions<ShopDbContext> options)
    {
        await using var db = new ShopDbContext(options);
        await db.Database.EnsureDeletedAsync();
    }

    // The older service scenarios use fixed product IDs. SQL Server's Products.Id
    // is an identity column, so enable explicit IDs only while seeding fixtures.
    private static async Task SaveSeedProducts(ShopDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [dbo].[Products] ON");
            try
            {
                await db.SaveChangesAsync();
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [dbo].[Products] OFF");
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
