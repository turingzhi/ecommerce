using Ecommerce.Features.Catalog.Contracts;
using Xunit;
namespace Ecommerce.Api.Tests.Catalog;
public class ProductBrowseParametersTests
{
    [Fact]
    public void BrowseNormalizesAndValidatesBounds()
    {
        Assert.True(ProductBrowseParameters.TryCreate("  Alpha  ",1000,2000,null,null,null,out var value,out _));
        Assert.Equal(new ProductBrowseParameters("Alpha",1000,2000,1,20,"idAsc"),value);
        Assert.True(ProductBrowseParameters.TryCreate(" ",null,null,200,50,"priceDesc",out value,out _));
        Assert.Null(value!.Category);Assert.Equal(9950,value.Offset);
        foreach(var input in new[]{(0,20,"idAsc"),(1,0,"idAsc"),(1,51,"idAsc"),(201,50,"idAsc"),(1,20,"relevance"),(1,20,"")})
            Assert.False(ProductBrowseParameters.TryCreate(null,null,null,input.Item1,input.Item2,input.Item3,out _,out _));
        Assert.False(ProductBrowseParameters.TryCreate(null,-1,null,null,null,null,out _,out _));
        Assert.False(ProductBrowseParameters.TryCreate(null,2000,1000,null,null,null,out _,out _));
    }
}
