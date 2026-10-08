using Ecommerce.Common.Controllers;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecommerce.Common.RateLimiting;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Services;
using Ecommerce.Features.Returns.Services;
using Ecommerce.Features.Shipments.Services;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecommerce.Tests.Http;

public class ControllerHttpTests : IClassFixture<ControllerHost>
{
    private readonly HttpClient client;
    private const string Id = "00000000-0000-0000-0000-000000000001";

    public ControllerHttpTests(ControllerHost host) => client = host.Client;

    public static IEnumerable<object[]> ProtectedRoutes()
    {
        yield return ["GET", "/auth/me"];
        yield return ["GET", "/orders"];
        yield return ["GET", $"/orders/{Id}"];
        yield return ["POST", "/orders"];
        yield return ["POST", $"/orders/{Id}/cancel"];
        yield return ["GET", "/cart"];
        yield return ["PUT", "/cart/items/1"];
        yield return ["DELETE", "/cart/items/1"];
        yield return ["DELETE", "/cart"];
        yield return ["POST", "/cart/checkout"];
        yield return ["GET", $"/payments/{Id}"];
        yield return ["GET", $"/orders/{Id}/payments"];
        yield return ["POST", $"/orders/{Id}/payments"];
        yield return ["GET", $"/refunds/{Id}"];
        yield return ["GET", $"/payments/{Id}/refunds"];
        yield return ["POST", $"/payments/{Id}/refunds"];
        yield return ["GET", $"/orders/{Id}/shipment"];
        yield return ["GET", $"/orders/{Id}/tracking"];
        yield return ["GET", $"/orders/{Id}/return"];
        yield return ["POST", $"/orders/{Id}/returns"];
        yield return ["GET", "/admin/products"];
        yield return ["POST", "/admin/products"];
        yield return ["PUT", "/admin/products/1"];
        yield return ["GET", "/admin/orders"];
        yield return ["GET", $"/admin/orders/{Id}"];
        yield return ["GET", "/admin/payments"];
        yield return ["GET", $"/admin/payments/{Id}"];
        yield return ["GET", "/admin/shipments"];
        yield return ["PUT", $"/admin/shipments/{Id}/status"];
        yield return ["GET", $"/admin/shipments/{Id}/history"];
        yield return ["GET", "/admin/returns"];
        yield return ["PUT", $"/admin/returns/{Id}/status"];
        yield return ["POST", $"/admin/returns/{Id}/refund"];
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Existing_routes_require_authentication(string method, string path)
    {
        using var response = await client.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/admin/products")]
    [InlineData("/admin/orders")]
    [InlineData("/admin/payments")]
    [InlineData("/admin/shipments")]
    [InlineData("/admin/returns")]
    public async Task Admin_reads_reject_customers_without_permission(string path)
    {
        using var request = CustomerRequest(HttpMethod.Get, path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"items\":null}", "Items or item entry is null")]
    [InlineData("{\"items\":[null]}", "Items or item entry is null")]
    [InlineData("{\"items\":[]}", "the number of items is smaller than 1 or bigger than 100")]
    [InlineData("{\"items\":[{\"productId\":0,\"quantity\":1}]}", "ProductId should be positive")]
    [InlineData("{\"items\":[{\"productId\":1,\"quantity\":0}]}", "Quantity should be between 1 and 100")]
    [InlineData("{\"items\":[{\"productId\":1,\"quantity\":1},{\"productId\":1,\"quantity\":1}]}", "There are some duplicate productIds")]
    public async Task Order_creation_preserves_explicit_validation_errors(string body, string error)
    {
        using var request = CustomerRequest(HttpMethod.Post, "/orders");
        request.Headers.Add("Idempotency-Key", "controller-validation");
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/products?minPriceCents=-1")]
    [InlineData("/products?sort=unknown")]
    [InlineData("/products?minPriceCents=abc")]
    public async Task Public_catalog_preserves_query_binding_and_validation(string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Current_user_preserves_claims_and_no_store()
    {
        using var request = CustomerRequest(HttpMethod.Get, "/auth/me");
        request.Headers.Add("X-Permissions", "orders:read,products:manage,orders:read");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("customer", json.RootElement.GetProperty("userId").GetString());
        Assert.Equal(["orders:read", "products:manage"],
            json.RootElement.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Liveness_remains_anonymous_with_no_store()
    {
        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Production", false, HttpStatusCode.NotFound)]
    [InlineData("Development", true, HttpStatusCode.Unauthorized)]
    public async Task Simulator_routes_and_UI_configuration_follow_environment(
        string environment, bool enabled, HttpStatusCode simulatorStatus)
    {
        await using var host = new ControllerHost(environment);
        await host.InitializeAsync();
        foreach (var feature in new[] { "payments", "refunds" })
        {
            using var response = await host.Client.PostAsync($"/dev/{feature}/{Id}/simulate", null);
            Assert.Equal(simulatorStatus, response.StatusCode);
        }
        using var config = await host.Client.GetAsync("/ui/config");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        Assert.True(config.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await config.Content.ReadAsStringAsync());
        Assert.Equal(enabled, json.RootElement.GetProperty("paymentSimulationEnabled").GetBoolean());
    }

    [Fact]
    public async Task Catalog_reads_still_share_the_search_rate_limit()
    {
        await using var host = new ControllerHost(searchPermits: 1);
        await host.InitializeAsync();
        using var first = await host.Client.GetAsync("/products?sort=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        using var second = await host.Client.GetAsync("/products/1");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.CacheControl?.NoStore);
        Assert.NotNull(second.Headers.RetryAfter);
    }

    private static HttpRequestMessage CustomerRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Customer-Id", "customer");
        return request;
    }
}

public sealed class ControllerHost : IAsyncLifetime, IAsyncDisposable
{
    private readonly string environment;
    private readonly int searchPermits;
    private WebApplication? app;
    public HttpClient Client { get; private set; } = null!;

    public ControllerHost() : this("Production") { }
    internal ControllerHost(string environment = "Production", int searchPermits = 1000)
    {
        this.environment = environment;
        this.searchPermits = searchPermits;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(OrderService).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddCommerceControllers(builder.Environment)
            .AddApplicationPart(typeof(OrderService).Assembly);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, CustomerAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(ProductAdministration.Policy, policy => policy.RequireClaim("permission", ProductAdministration.Permission));
            options.AddPolicy(OrderAdministration.Policy, policy => policy.RequireClaim("permission", OrderAdministration.Permission));
            options.AddPolicy(PaymentAdministration.Policy, policy => policy.RequireClaim("permission", PaymentAdministration.Permission));
            options.AddPolicy(ShipmentAdministration.Policy, policy => policy.RequireClaim("permission", ShipmentAdministration.Permission));
            options.AddPolicy(ReturnAdministration.Policy, policy => policy.RequireClaim("permission", ReturnAdministration.Permission));
        });
        builder.Services.AddCommerceRateLimiting(builder.Configuration);
        builder.Services.Configure<CommerceRateLimitOptions>(options => options.SearchPermitLimit = searchPermits);
        // Validation and authorization cases never execute SQL operations.
        builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlServer(
            "Server=127.0.0.1,1;Database=Unused;User Id=unused;Password=unused;TrustServerCertificate=True"));
        builder.Services.AddScoped<OrderService>();
        builder.Services.AddScoped<ProductQueryService>();
        app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapControllers();
        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Client = new HttpClient { BaseAddress = new Uri(address) };
    }

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null) await app.DisposeAsync();
    }
}

public sealed class CustomerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var id = Request.Headers["X-Customer-Id"].ToString();
        if (string.IsNullOrEmpty(id)) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id) };
        claims.AddRange(Request.Headers["X-Permissions"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(permission => new Claim("permission", permission)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
