using Ecommerce.Common.Security;
using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Features.Orders.Services;

public static class OrderAdministration
{
    public const string Policy = "OrderReader";
    public const string Permission = "orders:read";
    public static Task GrantAsync(UserManager<IdentityUser> users, string email) =>
        AdminPermissionGrant.GrantAsync(users, email, Permission);
}
