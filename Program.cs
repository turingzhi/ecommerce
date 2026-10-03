using Elastic.Clients.Elasticsearch;
using Ecommerce;
using Ecommerce.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

if (args.Contains("--verify"))
{
    await Verification.Run();
    return;
}

if (args.Contains("--verify-sqlserver"))
{
    await Verification.RunSqlServer();
    return;
}

if (args.Contains("--verify-rabbitmq"))
{
    await Verification.RunRabbitMq();
    return;
}

if (args.Contains("--verify-product-sync"))
{
    await Verification.RunProductSync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<ShopDb>();

builder.Services.AddAuthorization();
var connectionString =
    builder.Configuration.GetConnectionString("ShopDatabase")
    ?? throw new InvalidOperationException(
        "Connection string 'ShopDatabase' not found.");

builder.Services.AddDbContext<ShopDb>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<ProductCatalogService>();
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
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();

    await db.Database.MigrateAsync();

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

    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
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
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapHealthEndpoints();
app.MapProductEndpoints();
app.MapOrderEndpoints();
app.MapPaymentEndpoints();

app.Run();
