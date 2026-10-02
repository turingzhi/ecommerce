namespace Ecommerce;

public static class HealthEndpoints
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (ShopDb db) =>
        {
            try
            {
                return await db.Database.CanConnectAsync()
                    ? Results.Ok(new { status = "healthy" })
                    : Results.Json(new { status = "unhealthy" }, statusCode: 503);
            }
            catch (Exception)
            {
                return Results.Json(new { status = "unhealthy" }, statusCode: 503);
            }
        }).AllowAnonymous();

        return app;
    }
}
