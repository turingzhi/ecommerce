using Ecommerce.Common.Security;
using Microsoft.AspNetCore.Identity;
namespace Ecommerce.Features.Catalog.Services;
public static class ProductAdministration
{
    public const string Policy="CatalogAdmin";
    public const string Permission="products:manage";
    public static Task GrantAsync(UserManager<IdentityUser> users,string email)=>AdminPermissionGrant.GrantAsync(users,email,Permission);
}
