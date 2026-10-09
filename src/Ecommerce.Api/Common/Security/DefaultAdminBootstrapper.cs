using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Features.Orders.Services;
using Ecommerce.Features.Payments.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Ecommerce.Common.Security;

public sealed class DefaultAdminBootstrapper(UserManager<IdentityUser> users,
    IOptions<DefaultAdminOptions> options, ILogger<DefaultAdminBootstrapper> logger)
{
    public async Task EnsureAsync()
    {
        var settings = options.Value;
        if (!settings.Enabled) return;
        var email = settings.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
            throw new InvalidOperationException("DefaultAdmin:Email must be a valid email when setup is enabled.");

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(settings.Password))
                throw new InvalidOperationException("DefaultAdmin:Password is required to create the default admin account.");
            user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var created = await users.CreateAsync(user, settings.Password);
            if (!created.Succeeded)
                throw Failure("Could not create the default admin account", created);
        }
        // The configured password is only for initial creation. An existing
        // account retains its password, security stamp and profile on startup.
        var saved = await users.GetClaimsAsync(user);
        string[] permissions = [ProductAdministration.Permission,
            OrderAdministration.Permission, PaymentAdministration.Permission];
        var missing = permissions.Where(permission => !saved.Any(claim =>
                claim.Type == "permission" && claim.Value == permission))
            .Select(permission => new Claim("permission", permission)).ToArray();
        if (missing.Length > 0)
        {
            var granted = await users.AddClaimsAsync(user, missing);
            if (!granted.Succeeded)
                throw Failure("Could not grant default admin permissions", granted);
        }
        logger.LogInformation("Default admin account is ready with all current admin permissions.");
    }

    private static InvalidOperationException Failure(string message, IdentityResult result) =>
        new(message + ": " + string.Join(", ", result.Errors.Select(error => error.Code)));
}
