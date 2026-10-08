namespace Ecommerce.Features.Catalog.Contracts;
public record ProductBrowseParameters(string? Category,long? MinPriceCents,long? MaxPriceCents,int Page,int PageSize,string Sort)
{
    public int Offset=>(Page-1)*PageSize;
    public static bool TryCreate(string? category,long? min,long? max,int? page,int? pageSize,string? sort,out ProductBrowseParameters? value,out string? error)
    {
        value=null;error=null;category=string.IsNullOrWhiteSpace(category)?null:category.Trim();
        var p=page??1;var size=pageSize??20;sort??="idAsc";
        if(category?.Length>200) error="Category must contain at most 200 characters.";
        else if(min<0||max<0||min is not null&&max is not null&&min>max) error="Price bounds must be nonnegative and ordered.";
        else if(p<1||size<1||size>50||(long)p*size>10000) error="Page must be positive, pageSize 1–50, and the page within the 10,000-result window.";
        else if(sort is not ("idAsc" or "priceAsc" or "priceDesc")) error="Sort must be idAsc, priceAsc or priceDesc.";
        if(error is not null)return false;
        value=new(category,min,max,p,size,sort);return true;
    }
}
