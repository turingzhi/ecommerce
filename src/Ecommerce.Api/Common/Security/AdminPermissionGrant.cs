using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
namespace Ecommerce.Common.Security;
public static class AdminPermissionGrant
{
    public static async Task GrantAsync(UserManager<IdentityUser> users,string email,string permission)
    {
        var user=await users.FindByEmailAsync(email)??throw new ArgumentException("No registered account has that email.");
        if((await users.GetClaimsAsync(user)).Any(c=>c.Type=="permission"&&c.Value==permission)) return;
        var result=await users.AddClaimAsync(user,new Claim("permission",permission));
        if(!result.Succeeded) throw new InvalidOperationException("Could not grant permission: "+string.Join(", ",result.Errors.Select(e=>e.Description)));
    }
}
