using Elastic.Clients.Elasticsearch;

namespace Ecommerce.Search;

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
                .VersionType(VersionType.ExternalGte),
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

    public async Task<IReadOnlyCollection<ProductSearchDocument>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var response = await client.SearchAsync<ProductSearchDocument>(
            request => request
                .Indices(indexName)
                .Size(20)
                .AllowPartialSearchResults(false)
                .Query(q => q.MultiMatch(match => match
                    .Query(query.Trim())
                    .Fields(p => p.Name, p => p.Description))),
            cancellationToken);

        if (!response.IsValidResponse || response.TimedOut)
        {
            throw new ProductSearchUnavailableException(
                "Product search failed: " +
                response.DebugInformation);
        }

        return response.Documents.ToList();
    }

    private static bool IsTransient(int? status) =>
        status is null or 429 || status >= 500;
}
