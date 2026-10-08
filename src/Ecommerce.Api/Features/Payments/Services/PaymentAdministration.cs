using Ecommerce.Common.Security;
using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Features.Payments.Services;

public static class PaymentAdministration
{
    public const string Policy = "PaymentReader";
    public const string Permission = "payments:read";
    public static Task GrantAsync(UserManager<IdentityUser> users, string email) =>
        AdminPermissionGrant.GrantAsync(users, email, Permission);
}
