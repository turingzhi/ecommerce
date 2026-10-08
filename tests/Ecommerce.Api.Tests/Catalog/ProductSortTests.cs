using Ecommerce.Features.Catalog.Contracts;
using Xunit;
namespace Ecommerce.Api.Tests.Catalog;
public class ProductSortTests
{
    [Fact]
    public void NormalizedSortHasIndependentVersionedCacheIdentity()
    {
        ProductSearchParameters Parse(string? sort)
        {
            Assert.True(ProductSearchParameters.TryCreate("wireless",null,null,null,null,null,sort,out var value,out _));return value!;
        }
        Assert.Equal(Parse(null).CacheKey("3"),Parse("relevance").CacheKey("3"));
        Assert.StartsWith("products:search:v3:3:",Parse(null).CacheKey("3"));
        Assert.Equal(3,new[]{Parse(null),Parse("priceAsc"),Parse("priceDesc")}.Select(p=>p.CacheKey("3")).Distinct().Count());
        foreach(var sort in new[]{"","PriceAsc","idAsc"}) Assert.False(ProductSearchParameters.TryCreate("wireless",null,null,null,null,null,sort,out _,out _));
    }
}
