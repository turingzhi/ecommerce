using Ecommerce.Dtos;
using Ecommerce.Search;

namespace Ecommerce;

public static class ProductEndpoints
{
    public static WebApplication MapProductEndpoints(this WebApplication app)
    {
        app.MapGet("/products/search", async Task<IResult> (
            string? q,
            ProductSearchService search,
            ILogger<ProductSearchService> logger,
            CancellationToken cancellationToken) =>
        {
            var query = q?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length > 200)
            {
                return Results.BadRequest(new
                {
                    error = "Search query must contain between 1 and 200 characters."
                });
            }

            try
            {
                var products = await search.SearchAsync(query, cancellationToken);
                return Results.Ok(products.Select(ProductSearchResponse.From).ToList());
            }
            catch (ProductSearchUnavailableException exception)
            {
                logger.LogError(exception, "Product search is unavailable");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Product search is temporarily unavailable.",
                    detail: "Please try again later.");
            }
        }).AllowAnonymous();

        return app;
    }
}
