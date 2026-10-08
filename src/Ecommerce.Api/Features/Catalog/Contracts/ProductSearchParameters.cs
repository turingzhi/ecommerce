using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ecommerce.Features.Catalog.Contracts;

public record ProductSearchParameters(
    string Query,
    string? Category,
    long? MinPriceCents,
    long? MaxPriceCents,
    int Page,
    int PageSize,
    string Sort = "relevance")
{
    public int Offset => (Page - 1) * PageSize;

    public static bool TryCreate(string? query,string? category,long? minPriceCents,long? maxPriceCents,int? page,int? pageSize,
        out ProductSearchParameters? parameters,out string? error)=>
        TryCreate(query,category,minPriceCents,maxPriceCents,page,pageSize,null,out parameters,out error);

    public static bool TryCreate(
        string? query, string? category, long? minPriceCents, long? maxPriceCents,
        int? page, int? pageSize, string? sort,
        out ProductSearchParameters? parameters, out string? error)
    {
        parameters = null;
        error = null;
        query = query?.Trim();
        category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        sort ??= "relevance";
        var currentPage = page ?? 1;
        var currentSize = pageSize ?? 20;
        if (string.IsNullOrEmpty(query) || query.Length > 200)
            error = "Search query must contain between 1 and 200 characters.";
        else if (category?.Length > 200)
            error = "Category must contain at most 200 characters.";
        else if (minPriceCents < 0 || maxPriceCents < 0)
            error = "Price bounds must be nonnegative integer cents.";
        else if (minPriceCents is not null && maxPriceCents is not null && minPriceCents > maxPriceCents)
            error = "Minimum price must not exceed maximum price.";
        else if (currentPage < 1 || currentSize < 1 || currentSize > 50)
            error = "Page must be positive and pageSize must be between 1 and 50.";
        // Elasticsearch's default from/size result window is 10,000.
        // Use long arithmetic before calculating the validated int offset.
        else if ((long)currentPage * currentSize > 10_000)
            error = "Requested page exceeds the 10,000-result search window.";

        if(error is null && sort is not ("relevance" or "priceAsc" or "priceDesc"))
            error="Sort must be relevance, priceAsc or priceDesc.";

        if (error is not null)
            return false;
        parameters = new(query!, category, minPriceCents, maxPriceCents, currentPage, currentSize, sort);
        return true;
    }

    public string CacheKey(string generation)
    {
        // JSON preserves parameter boundaries; hashing keeps keys bounded and
        // prevents delimiter collisions between query and category values.
        var payload = JsonSerializer.Serialize(this);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return $"products:search:v3:{generation}:{hash}";
    }
}
