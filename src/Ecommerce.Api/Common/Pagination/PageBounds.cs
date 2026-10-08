namespace Ecommerce.Common.Pagination;
public record PageBounds(int Page, int PageSize, long Offset)
{
    public static PageBounds Normalize(int? page, int? pageSize)
    {
        var p = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 20, 1, 50);
        return new(p,size,(long)(p-1)*size);
    }
}
