using Ecommerce;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

if (args.Contains("--verify"))
{
    await Verification.Run();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddIdentityApiEndpoints<IdentityUser>()
    .AddEntityFrameworkStores<ShopDb>();

builder.Services.AddAuthorization();
var databasePath = builder.Configuration["Ecommerce:DatabasePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "ecommerce-identity.db");
builder.Services.AddDbContext<ShopDb>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<RefundService>();
builder.Services.AddHostedService<OrderExpirationWorker>();
builder.Services.AddScoped<IEventPublisher, LoggingEventPublisher>();
builder.Services.AddScoped<EventConsumer>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();
    // await db.Database.EnsureCreatedAsync();
    await db.Database.MigrateAsync();
    if (!await db.Products.AnyAsync())
    {
        db.Products.Add(new Product { Id = 1, PriceCents = 5000, Available = 10 });
        db.Products.Add(new Product { Id = 2, PriceCents = 2000, Available = 10 });
        await db.SaveChangesAsync();
    }
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapHealthEndpoints();
app.MapOrderEndpoints();
app.MapPaymentEndpoints();

app.Run();
