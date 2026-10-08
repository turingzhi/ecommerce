using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Orders.Models;
using Ecommerce.Observability;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace Ecommerce.Infrastructure.Search;

public class ProductSearchService(ElasticsearchClient client, string indexName = "products")
{
    public async Task IndexProductAsync(
        Product product,
        CancellationToken cancellationToken = default)
    {
        await IndexDocumentAsync(ProductSearchDocument.FromProduct(product), cancellationToken);
    }

    public async Task IndexDocumentAsync(
        ProductSearchDocument document,
        CancellationToken cancellationToken = default)
    {

        var response = await client.IndexAsync(
            document,
            request => request
                .Index(indexName)
                .Id(document.Id)
                .Version(document.Version)
                .VersionType(VersionType.ExternalGte)
                .Refresh(Refresh.WaitFor),
                cancellationToken);

        // A newer version is already in Elasticsearch. The stale event is complete.
        if (response.ApiCallDetails?.HttpStatusCode == 409)
            return;

        if (!response.IsValidResponse)
        {
            var error = $"Failed to index product {document.Id}: " +
                response.DebugInformation;
            if (IsTransient(response.ApiCallDetails?.HttpStatusCode))
                throw new ProductSearchUnavailableException(error);
            throw new InvalidOperationException(error);
        }
    }

    public async Task EnsureIndexAsync(
        CancellationToken cancellationToken = default)
    {
        var exists = await client.Indices.ExistsAsync(
            indexName, cancellationToken);

        if (exists.Exists)
        {
            return;
        }

        // 404 means the index does not exist yet.
        if (!exists.IsValidResponse &&
            exists.ApiCallDetails?.HttpStatusCode != 404)
        {
            var error = "Failed to check the products index: " +
                exists.DebugInformation;
            if (IsTransient(exists.ApiCallDetails?.HttpStatusCode))
                throw new ProductSearchUnavailableException(error);
            throw new InvalidOperationException(error);
        }

        var response = await client.Indices
            .CreateAsync<ProductSearchDocument>(
                request => request
                    .Index(indexName)
                    .Mappings(mapping => mapping
                        .Properties(properties => properties
                            .IntegerNumber(p => p.Id)
                            .Text(p => p.Name)
                            .Text(p => p.Description)
                            .Keyword(p => p.Category)
                            .LongNumber(p => p.PriceCents)
                            .LongNumber(p => p.Version))),
                cancellationToken);

        if (!response.IsValidResponse)
        {
            var error = "Failed to create the products index: " +
                response.DebugInformation;
            if (IsTransient(response.ApiCallDetails?.HttpStatusCode))
                throw new ProductSearchUnavailableException(error);
            throw new InvalidOperationException(error);
        }
    }

    private async Task<ProductSearchResult> SearchAsyncMeasuredCore(
        ProductSearchParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var filters = new List<Query>();
        if (parameters.Category is not null)
            filters.Add(new TermQuery { Field = "category", Value = parameters.Category });
        if (parameters.MinPriceCents is not null || parameters.MaxPriceCents is not null)
        {
            // Untyped bounds preserve integer cents rather than converting long
            // prices to doubles, which can round large values.
            filters.Add(new UntypedRangeQuery
            {
                Field = "priceCents",
                Gte = parameters.MinPriceCents,
                Lte = parameters.MaxPriceCents
            });
        }

        var response = await client.SearchAsync<ProductSearchDocument>(
            request => request
                .Indices(indexName)
                .From(parameters.Offset)
                .Size(parameters.PageSize)
                .TrackTotalHits(true)
                .AllowPartialSearchResults(false)
                .Query(q => q.Bool(b => b
                    .Must(m => m.MultiMatch(match => match
                        .Query(parameters.Query)
                        .Fields(p => p.Name, p => p.Description)))
                    .Filter(filters)))
                .Sort(sort =>
                {
                    if(parameters.Sort=="priceAsc") sort.Field(p=>p.PriceCents,field=>field.Order(SortOrder.Asc));
                    else if(parameters.Sort=="priceDesc") sort.Field(p=>p.PriceCents,field=>field.Order(SortOrder.Desc));
                    else sort.Score(score=>score.Order(SortOrder.Desc));
                },
                      sort => sort.Field(p => p.Id, field => field.Order(SortOrder.Asc))),
            cancellationToken);

        if (!response.IsValidResponse || response.TimedOut)
            throw new ProductSearchUnavailableException(
                "Product search failed: " + response.DebugInformation);

        return new ProductSearchResult(response.Documents.ToList(), response.Total);
    }

    private static bool IsTransient(int? status) =>
        status is null or 429 || status >= 500;

    public Task<ProductSearchResult> SearchAsync(
        ProductSearchParameters parameters,
        CancellationToken cancellationToken = default) => Ecommerce.Observability.CommerceTelemetry.MeasureAsync("search","elasticsearch",()=>SearchAsyncMeasuredCore(parameters,cancellationToken),_=>"success");
}

public record ProductSearchResult(IReadOnlyList<ProductSearchDocument> Products, long Total);
