using Microsoft.AspNetCore.Identity;

namespace Ecommerce;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        app.MapGroup("/auth").MapIdentityApi<IdentityUser>();
        return app;
    }
}
