using Ecommerce.Common.Security;
using Microsoft.AspNetCore.Identity;
namespace Ecommerce.Features.Returns.Services;
public static class ReturnAdministration
{
    public const string Policy="ReturnAdmin";
    public const string Permission="returns:manage";
    public static Task GrantAsync(UserManager<IdentityUser> users,string email)=>AdminPermissionGrant.GrantAsync(users,email,Permission);
}
