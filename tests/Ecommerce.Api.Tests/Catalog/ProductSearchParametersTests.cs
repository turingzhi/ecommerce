using Ecommerce.Features.Catalog.Contracts;
using Xunit;

namespace Ecommerce.Api.Tests.Catalog;

public class ProductSearchParametersTests
{
    private static ProductSearchParameters Parse(
        string query = "wireless", string? category = null,
        long? min = null, long? max = null, int? page = null, int? size = null)
    {
        Assert.True(ProductSearchParameters.TryCreate(
            query, category, min, max, page, size, out var result, out var error), error);
        return result!;
    }

    [Fact]
    public void EquivalentRequestsShareACacheKey()
    {
        Assert.Equal(Parse().CacheKey("3"),
            Parse("  wireless  ", "   ", page: 1, size: 20).CacheKey("3"));
    }

    [Fact]
    public void EverySearchDimensionAndGenerationHasAnIndependentCacheEntry()
    {
        var original = Parse().CacheKey("3");
        var variants = new[]
        {
            Parse("mouse"), Parse(category: "Audio"), Parse(min: 1000),
            Parse(max: 2000), Parse(page: 2), Parse(size: 10)
        };
        Assert.All(variants, value => Assert.NotEqual(original, value.CacheKey("3")));
        Assert.NotEqual(original, Parse().CacheKey("4"));
        Assert.Equal(variants.Length, variants.Select(value => value.CacheKey("3")).Distinct().Count());
    }

    [Fact]
    public void DelimitersInValuesDoNotCauseCacheCollisions()
    {
        Assert.NotEqual(Parse("a:b", "c").CacheKey("3"),
                        Parse("a", "b:c").CacheKey("3"));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    [InlineData(201, 50)]
    [InlineData(int.MaxValue, 50)]
    public void InvalidPagesAreRejectedWithoutOverflow(int page, int size)
    {
        Assert.False(ProductSearchParameters.TryCreate(
            "wireless", null, null, null, page, size, out var result, out var error));
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void LastSupportedPageHasASafeOffset()
    {
        Assert.Equal(9950, Parse(page: 200, size: 50).Offset);
    }

    [Fact]
    public void PriceBoundsRemainExactLongIntegers()
    {
        var parameters = Parse(min: 9007199254740993, max: long.MaxValue);
        Assert.Equal(9007199254740993L, parameters.MinPriceCents);
        Assert.Equal(long.MaxValue, parameters.MaxPriceCents);
        Assert.NotEqual(parameters.CacheKey("3"),
                        Parse(min: 9007199254740992, max: long.MaxValue).CacheKey("3"));
    }
}
