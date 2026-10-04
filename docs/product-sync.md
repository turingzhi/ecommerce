# Product synchronization and search

[Documentation index](README.md) · [Project overview](../README.md)

SQL Server is the catalog's source of truth. Elasticsearch holds a searchable copy and can briefly lag behind a write. The HTTP [search endpoint](api.md#product-search) queries Elasticsearch; checkout reads current price and stock from SQL Server.

## Catalog write path

`ProductCatalogService.CreateAsync` creates a product and a `ProductUpserted` Outbox event in one SQL Server transaction. `UpdateAsync` changes name, description, category, or price and saves a new event with a higher version. It leaves stock untouched so a catalog edit cannot undo a concurrent checkout. Direct SQL edits bypass this flow.

With the API running, a separate terminal can create or update a product through the local CLI. Set `ConnectionStrings:ShopDatabase` for SQL Server and start RabbitMQ and Elasticsearch first; alternatively, use the [Compose commands in the README](../README.md#catalog-and-search).

```sh
dotnet run -- --create-product "Camera" "Wide angle" "Photo" 12000 3
dotnet run -- --update-product 3 "Camera II" "Wide angle" "Photo" 13000
```

The final value on create is starting stock. The update command changes searchable details only. Replace `3` with the ID printed by create. The commands exit after writing SQL and Outbox; the running API's Outbox worker publishes the event on a later poll. For products that existed before automatic synchronization, run `dotnet run -- --index-products` once to backfill them. Product deletion is not synchronized.

## Elasticsearch copy

An Elasticsearch **index** holds product **documents**, analogous to a collection of searchable records. Each document contains product ID, name, description, category, price in cents, and version. The mapping uses analyzed `text` fields for name and description, a `keyword` field for category, and numeric fields for ID, price, and version. Search currently runs a multi-match query across name and description and returns at most 20 hits. An inverted index helps Elasticsearch find documents matching analyzed terms; this is why it is useful for text search. It is not the authority for checkout stock or price.

The RabbitMQ consumer writes each snapshot with the product's SQL version as an external version. A late older event receives a version conflict and is treated as superseded; repeating the same event is safe. The consumer saves `ProcessedMessages` only after Elasticsearch accepts the write, then acknowledges RabbitMQ. Temporary Elasticsearch outages keep the message in the durable retry queue until recovery. Invalid data or another non-transient failure can reach the RabbitMQ dead-letter queue after five failed processing attempts. See [RabbitMQ delivery](rabbitmq.md) for the two separate retry stages.

`dotnet run -- --verify-product-sync` checks SQL and Outbox atomicity, stock preservation, version ordering, deduplication, search outage recovery, and the full SQL Server → Outbox → RabbitMQ → Elasticsearch path. It requires `ECOMMERCE_SQLSERVER` plus local RabbitMQ and Elasticsearch. See [verification](verification.md) for setup and the [local demonstration](demo.md) for an outage and recovery walkthrough.
