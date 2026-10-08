using Ecommerce.Common.Security;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Features.Shipments.Services;

public static class ShipmentAdministration
{
    public const string Policy = "ShipmentAdmin";
    public const string ClaimType = "permission";
    public const string Permission = "shipments:manage";

    // Invoked only by the operator CLI, never by a public HTTP endpoint.
    public static Task GrantAsync(UserManager<IdentityUser> users,string email)=>AdminPermissionGrant.GrantAsync(users,email,Permission);
}
