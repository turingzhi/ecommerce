# Product synchronization and search

[Documentation index](README.md) · [Project overview](../README.md)

SQL Server owns the catalog. Elasticsearch holds a searchable copy and can briefly lag behind writes. Public browse/detail routes read SQL; `/products/search` reads Elasticsearch with a Redis response cache. Checkout checks SQL price and stock.

## Catalog write path

`ProductCatalogService.CreateAsync` commits a product and `ProductUpserted` Outbox event in one transaction. Updates change name, description, category, or price, increase the product version, and save another snapshot event. They leave stock untouched, preserving concurrent checkout changes. Direct SQL edits bypass synchronization; product deletion is not synchronized.

Catalog operators can create/update through `/admin/products`. HTTP updates require the saved `expectedVersion`; stale edits return 409. See [admin access](admin.md) and [API reference](api.md).

With Compose running, use the CLI from the repository root:

```sh
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --create-product "Camera" "Wide angle" "Photo" 12000 3
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --update-product 3 "Camera II" "Wide angle" "Photo" 13000
```

Prices are integer cents; the final create argument is starting stock. Replace update ID `3` with the ID printed by create. The CLI exits after writing SQL and Outbox; the running API publishes the event on a later poll. CLI updates do not take an `expectedVersion`.

For host execution, configure the SQL connection as described in [Docker operations](docker.md), then use:

```sh
dotnet run --project src/Ecommerce.Api -- --create-product "Camera" "Wide angle" "Photo" 12000 3
dotnet run --project src/Ecommerce.Api -- --update-product 3 "Camera II" "Wide angle" "Photo" 13000
```

## Elasticsearch copy

Each document contains ID, name, description, category, price in cents, and version. Name/description use analyzed `text` fields, category uses `keyword`, and ID/price/version use numeric fields. A text query searches name and description; category matches exactly, and optional price bounds are inclusive.

| Search option | Behavior |
| --- | --- |
| `q` | Trimmed text, 1–200 characters |
| `category` | Optional trimmed exact category, at most 200 characters |
| `minPriceCents`, `maxPriceCents` | Nonnegative integer cents; minimum must not exceed maximum |
| `page`, `pageSize` | Defaults 1 and 20; size 1–50; `page × pageSize` must not exceed 10,000 |
| `sort` | `relevance` (default), `priceAsc`, or `priceDesc`; ties use ascending product ID |

A successful response includes products, exact matching total, page, and pageSize. Elasticsearch failures/timeouts become 503 when no cached result is available. Partial search results are disabled.

The consumer writes snapshots by product ID with SQL version and Elasticsearch `external_gte` versioning. Equal versions are safe to repeat; an older version's 409 is treated as superseded. Successful writes use `Refresh.WaitFor`, then increment the Redis search generation and save the SQL processed ID. This keeps old event delivery from overwriting newer catalog details. Temporary Elasticsearch outages remain retryable; other failures follow the [RabbitMQ retry policy](rabbitmq.md). See [Redis invalidation](redis.md#automatic-invalidation).

Sources: [ProductCatalogService](../src/Ecommerce.Api/Features/Catalog/Services/ProductCatalogService.cs), [ProductSearchService](../src/Ecommerce.Api/Infrastructure/Search/ProductSearchService.cs), and [search parameters](../src/Ecommerce.Api/Features/Catalog/Contracts/ProductSearchParameters.cs).

## Backfill and verification

For preexisting products or a lost Elasticsearch index, copy current SQL products once:

```sh
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --index-products
```

Backfill ensures the index and writes current versions directly. It does not publish Outbox events or advance the Redis generation, so existing search cache entries can last up to 30 seconds.

`--verify-product-sync` checks SQL/Outbox atomicity, stock preservation, version ordering, deduplication, outage recovery, and SQL → RabbitMQ → Elasticsearch delivery. It needs SQL Server, RabbitMQ, and Elasticsearch. See [verification](verification.md) and the [demo walkthrough](demo.md).
