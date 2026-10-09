using System.Security.Claims;
using Ecommerce.Common.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecommerce.Api.Tests.Accounts;

public class DefaultAdminBootstrapperTests
{
    private const string Email = "admin@example.test";
    private const string Password = "OriginalAdmin!123456";
    private static readonly string[] ExpectedPermissions =
        ["orders:read", "payments:read", "products:manage"];

    [Fact]
    public async Task DisabledSetupDoesNotCreateAnAccount()
    {
        using var fixture = new IdentityFixture();
        await fixture.Seed(new() { Enabled = false, Email = "invalid", Password = "invalid" });
        Assert.Empty(fixture.Store.Users);
    }

    [Fact]
    public async Task CreatesAccountWithHashedPasswordAndAllCurrentPermissions()
    {
        using var fixture = new IdentityFixture();
        await fixture.Seed(new() { Enabled = true, Email = "  " + Email + "  ", Password = Password });
        var user = Assert.Single(fixture.Store.Users.Values);
        Assert.Equal(Email, user.Email);
        Assert.True(await fixture.Users.CheckPasswordAsync(user, Password));
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.Equal(ExpectedPermissions, (await fixture.Users.GetClaimsAsync(user))
            .Where(claim => claim.Type == "permission").Select(claim => claim.Value).Order().ToArray());
    }

    [Fact]
    public async Task RepeatedStartupPreservesAccountPasswordAndUnrelatedClaims()
    {
        using var fixture = new IdentityFixture();
        var user = new IdentityUser { UserName = Email, Email = Email };
        Assert.True((await fixture.Users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await fixture.Users.AddClaimAsync(user, new Claim("permission", "custom:read"))).Succeeded);
        var hash = user.PasswordHash;
        var options = new DefaultAdminOptions { Enabled = true, Email = Email, Password = "DifferentAdmin!123456" };
        await fixture.Seed(options);
        await fixture.Seed(options);
        Assert.Single(fixture.Store.Users);
        Assert.Equal(hash, user.PasswordHash);
        Assert.True(await fixture.Users.CheckPasswordAsync(user, Password));
        Assert.False(await fixture.Users.CheckPasswordAsync(user, options.Password));
        var claims = await fixture.Users.GetClaimsAsync(user);
        Assert.Equal(4, claims.Count);
        Assert.Contains(claims, claim => claim.Type == "permission" && claim.Value == "custom:read");
        Assert.Equal(ExpectedPermissions, claims.Where(claim => claim.Type == "permission" && claim.Value != "custom:read")
            .Select(claim => claim.Value).Order().ToArray());
    }

    [Fact]
    public async Task ExistingAccountCanStartAfterBootstrapPasswordIsRemoved()
    {
        using var fixture = new IdentityFixture();
        await fixture.Seed(new() { Enabled = true, Email = Email, Password = Password });
        await fixture.Seed(new() { Enabled = true, Email = Email, Password = "" });
        var user = Assert.Single(fixture.Store.Users.Values);
        Assert.True(await fixture.Users.CheckPasswordAsync(user, Password));
        Assert.Equal(3, (await fixture.Users.GetClaimsAsync(user)).Count);
    }

    [Theory]
    [InlineData("", "OriginalAdmin!123456")]
    [InlineData("not-an-email", "OriginalAdmin!123456")]
    [InlineData("admin@example.test", "")]
    [InlineData("admin@example.test", "weak")]
    public async Task InvalidNewAccountSettingsFailWithoutCreatingAnAccountOrExposingPassword(string email, string password)
    {
        using var fixture = new IdentityFixture();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Seed(new() { Enabled = true, Email = email, Password = password }));
        Assert.Empty(fixture.Store.Users);
        if (password.Length > 0) Assert.DoesNotContain(password, error.Message);
    }

    private sealed class IdentityFixture : IDisposable
    {
        private readonly ServiceProvider services;
        public MemoryIdentityStore Store { get; }
        public UserManager<IdentityUser> Users { get; }
        public IdentityFixture()
        {
            var collection = new ServiceCollection().AddLogging();
            collection.AddIdentityCore<IdentityUser>(options => options.User.RequireUniqueEmail = true)
                .AddUserStore<MemoryIdentityStore>();
            services = collection.BuildServiceProvider();
            Store = (MemoryIdentityStore)services.GetRequiredService<IUserStore<IdentityUser>>();
            Users = services.GetRequiredService<UserManager<IdentityUser>>();
        }
        public Task Seed(DefaultAdminOptions options) => new DefaultAdminBootstrapper(
            Users, Options.Create(options), NullLogger<DefaultAdminBootstrapper>.Instance).EnsureAsync();
        public void Dispose() => services.Dispose();
    }
}
