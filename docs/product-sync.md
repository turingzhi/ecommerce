# Automatic product synchronization

`ProductCatalogService.CreateAsync` creates a product and a `ProductUpserted` Outbox event in one SQL Server transaction. `UpdateAsync` changes name, description, category, or price and saves a new event with a higher version. It leaves stock untouched so a catalog edit cannot undo a concurrent checkout. Direct SQL edits bypass this flow.

Run the API with `ConnectionStrings:ShopDatabase` configured and `docker compose up -d rabbitmq elasticsearch` (create `.env` from `.env.example` first). With the API running, a separate terminal can make a change through the local commands:

```sh
dotnet run -- --create-product "Camera" "Wide angle" "Photo" 12000 3
dotnet run -- --update-product 3 "Camera II" "Wide angle" "Photo" 13000
```

The final value on create is starting stock. The update command changes searchable details only. The commands exit after writing SQL and Outbox; the running API's Outbox worker publishes the event within its normal polling interval. For products that existed before this migration, run `dotnet run -- --index-products` once to backfill them.

The RabbitMQ consumer writes the snapshot to Elasticsearch with the product's SQL version as an external version. A late older event receives a version conflict and is treated as already superseded; repeating the same event is safe. The consumer saves `ProcessedMessages` only after Elasticsearch accepts the write, then acknowledges RabbitMQ. Temporary Elasticsearch outages keep the message in the durable retry queue until recovery; invalid data or other non-transient failures still reach the dead-letter queue after five attempts.

`dotnet run -- --verify-product-sync` checks SQL and Outbox atomicity, stock preservation, version ordering, deduplication, search outage recovery, and the full SQL Server → Outbox → RabbitMQ → Elasticsearch path. It requires `ECOMMERCE_SQLSERVER` plus the local RabbitMQ and Elasticsearch containers.

For a verified Compose walkthrough that needs no host connection string, see [the successful-flow and outage/recovery demo](demo.md).
