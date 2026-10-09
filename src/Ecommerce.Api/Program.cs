using Ecommerce.Common.Controllers;
using Ecommerce.Common.Security;
using Ecommerce.Common.RateLimiting;
using Ecommerce.Observability;
using Ecommerce.Features.Cart.Services;
using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Refunds.Services;
using Ecommerce.Infrastructure.Health;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Messaging.Outbox;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Search;
using Ecommerce.Verification;
using Elastic.Clients.Elasticsearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

if (args.Contains("--verify"))
{
    await VerificationRunner.Run();
    return;
}

if (args.Contains("--verify-sqlserver"))
{
    await VerificationRunner.RunSqlServer();
    return;
}

if (args.Contains("--verify-rabbitmq"))
{
    await VerificationRunner.RunRabbitMq();
    return;
}

if (args.Contains("--verify-product-sync"))
{
    await VerificationRunner.RunProductSync();
    return;
}

// Shipment and return CLI operations are disabled with their HTTP features.
if (args.Any(arg => arg is "--verify-shipments" or "--verify-returns"
    or "--grant-shipment-admin" or "--grant-return-admin"))
    throw new ArgumentException("Shipment and return features are disabled.");

if (args.Contains("--verify-catalog")) { await VerificationRunner.RunCatalog(); return; }

if (args.Contains("--verify-telemetry")) { await VerificationRunner.RunTelemetry(); return; }

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCommerceObservability(builder.Configuration);
builder.Services.AddCommerceControllers(builder.Environment);
builder.Services.Configure<DefaultAdminOptions>(builder.Configuration.GetSection("DefaultAdmin"));
builder.Services.AddScoped<DefaultAdminBootstrapper>();
builder.Services
    .AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<ShopDbContext>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ProductAdministration.Policy, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", ProductAdministration.Permission));
    options.AddPolicy(OrderAdministration.Policy, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", OrderAdministration.Permission));
    options.AddPolicy(PaymentAdministration.Policy, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", PaymentAdministration.Permission));
});
builder.Services.AddCommerceRateLimiting(builder.Configuration);
var connectionString =
    builder.Configuration.GetConnectionString("ShopDatabase")
    ?? throw new InvalidOperationException(
        "Connection string 'ShopDatabase' not found.");

builder.Services.AddDbContext<ShopDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<OrderQueryService>();
builder.Services.AddScoped<PaymentQueryService>();
builder.Services.AddScoped<CartService>();
builder.Services.AddScoped<ProductCatalogService>();
builder.Services.AddScoped<ProductQueryService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<RefundService>();
builder.Services.AddHostedService<OrderExpirationWorker>();
builder.Services.Configure<RabbitMqOptions>(
    builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();
builder.Services.AddScoped<EventConsumer>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<RabbitMqConsumerWorker>();
var elasticsearchUrl = builder.Configuration["Elasticsearch:Url"]
    ?? "http://localhost:9200";

builder.Services.AddSingleton<ElasticsearchClient>(_ =>
{
    var settings = new ElasticsearchClientSettings(
        new Uri(elasticsearchUrl))
        .DefaultIndex("products");

    return new ElasticsearchClient(settings);
});
builder.Services.AddScoped<ProductSearchService>();
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
    StackExchange.Redis.ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? "localhost:6379,abortConnect=false"));
builder.Services.AddHealthChecks()
    .AddCheck<SqlServerHealthCheck>("sqlserver", timeout: TimeSpan.FromSeconds(5))
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", timeout: TimeSpan.FromSeconds(5))
    .AddCheck<ElasticsearchHealthCheck>("elasticsearch", timeout: TimeSpan.FromSeconds(5))
    .AddCheck<RedisHealthCheck>("redis", timeout: TimeSpan.FromSeconds(5));
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();

    await db.Database.MigrateAsync();

    var productGrantAt = Array.IndexOf(args, "--grant-product-admin");
    if (productGrantAt >= 0)
    {
        if (args.Length != productGrantAt + 2 || string.IsNullOrWhiteSpace(args[productGrantAt + 1])) throw new ArgumentException("Usage: --grant-product-admin REGISTERED_EMAIL");
        await ProductAdministration.GrantAsync(scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(), args[productGrantAt + 1]);
        Console.WriteLine("Catalog admin permission granted. Log in again for a new bearer token."); return;
    }
    foreach (var flag in new[] { "--grant-order-reader", "--grant-payment-reader" })
    {
        var readerAt = Array.IndexOf(args, flag);
        if (readerAt < 0) continue;
        if (args.Length != readerAt + 2 || string.IsNullOrWhiteSpace(args[readerAt + 1]))
            throw new ArgumentException($"Usage: {flag} REGISTERED_EMAIL");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        if (flag == "--grant-order-reader") await OrderAdministration.GrantAsync(users, args[readerAt + 1]);
        else await PaymentAdministration.GrantAsync(users, args[readerAt + 1]);
        Console.WriteLine("Read permission granted. Log in again for a new bearer token.");
        return;
    }
    await scope.ServiceProvider.GetRequiredService<DefaultAdminBootstrapper>().EnsureAsync();

    if (!await db.Products.AnyAsync())
    {
        var catalog = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
        await catalog.CreateAsync(new ProductChange(
            "Wireless Headphones", "Bluetooth headphones with noise cancellation",
            "Audio", 5000, 10));
        await catalog.CreateAsync(new ProductChange(
            "Wireless Mouse", "Compact wireless mouse for laptops and desktop computers",
            "Accessories", 2000, 10));
    }
}
var createAt = Array.IndexOf(args, "--create-product");
var updateAt = Array.IndexOf(args, "--update-product");
if (createAt >= 0 || updateAt >= 0)
{
    var at = createAt >= 0 ? createAt : updateAt;
    var updating = updateAt >= 0;
    var expected = 5;
    var available = 0;
    if (args.Length - at - 1 != expected ||
        !long.TryParse(args[at + (updating ? 5 : 4)], out var priceCents) ||
        (!updating && !int.TryParse(args[at + 5], out available)) ||
        (updating && !int.TryParse(args[at + 1], out _)))
    {
        throw new ArgumentException(updating
            ? "Usage: --update-product ID NAME DESCRIPTION CATEGORY PRICE_CENTS"
            : "Usage: --create-product NAME DESCRIPTION CATEGORY PRICE_CENTS AVAILABLE");
    }

    using var scope = app.Services.CreateScope();
    var catalog = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
    var product = updating
        ? await catalog.UpdateAsync(int.Parse(args[at + 1]),
            new ProductDetailsChange(args[at + 2], args[at + 3], args[at + 4], priceCents))
        : await catalog.CreateAsync(new ProductChange(
            args[at + 1], args[at + 2], args[at + 3], priceCents, available));
    Console.WriteLine($"Saved product {product.Id} at version {product.Version}; its Outbox event will be delivered by the running API worker.");
    return;
}
if (args.Contains("--index-products"))
{
    using var scope = app.Services.CreateScope();

    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    var search = scope.ServiceProvider
        .GetRequiredService<ProductSearchService>();

    await search.EnsureIndexAsync();

    var products = await db.Products
        .AsNoTracking()
        .ToListAsync();

    foreach (var product in products)
    {
        await search.IndexProductAsync(product);
    }

    Console.WriteLine($"Indexed {products.Count} products.");
    return;
}
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Identity supplies its standard registration, login, and refresh routes.
app.MapGroup("/auth").MapIdentityApi<IdentityUser>();
app.MapControllers();

app.Run();
