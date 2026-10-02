using System.Security.Claims;

namespace Ecommerce;

public static class PaymentEndpoints
{
    public static WebApplication MapPaymentEndpoints(this WebApplication app)
    {
        app.MapPost("/orders/{orderId:guid}/payments", async (
            HttpRequest http,
            Guid orderId,
            PaymentService service,
            ClaimsPrincipal user) =>
        {
            var customerId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(customerId))
                return Results.Unauthorized();

            var key = http.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
                return Results.BadRequest(new { error = "Invalid Idempotency Key" });

            var result = await service.Create(customerId, orderId, key);
            if (result.Error is not null)
                return Results.Conflict(new { error = result.Error });
            return result.Replayed
                ? Results.Ok(result.Payment)
                : Results.Json(result.Payment, statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization();

        return app;
    }
}
