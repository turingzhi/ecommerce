using Ecommerce.Common.Pagination;
using Xunit;
namespace Ecommerce.Api.Tests.Common;
public class PageBoundsTests
{
    [Fact]
    public void PagingNormalizesAndAvoidsOverflow()
    {
        Assert.Equal(new PageBounds(1,20,0), PageBounds.Normalize(null,null));
        Assert.Equal(new PageBounds(1,1,0), PageBounds.Normalize(0,0));
        Assert.Equal(50, PageBounds.Normalize(1,1000).PageSize);
        Assert.Equal(107374182300L, PageBounds.Normalize(int.MaxValue,50).Offset);
    }
}
