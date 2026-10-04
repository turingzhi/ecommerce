# Verification

The repository uses custom runners selected by application arguments. It does not have a separate `dotnet test` project. Run commands from the repository root. The [CI workflow](../.github/workflows/ecommerce.yml) builds the application, runs the SQLite checks, then starts a local Compose stack for the other three runners.

## SQLite service checks

```sh
dotnet run -- --verify
```

This runner covers order and payment rules, idempotency, cancellation, expiration, refunds, Outbox behavior, and other service-level scenarios using temporary SQLite databases. It does not exercise SQL Server's locking semantics or the public HTTP endpoints.

## SQL Server and infrastructure checks

Copy `.env.example` to `.env`, set `MSSQL_SA_PASSWORD`, and start the local stack:

```sh
cp -n .env.example .env
# Set MSSQL_SA_PASSWORD in .env.
docker compose up -d --build --wait
```

Set `ECOMMERCE_SQLSERVER` in the terminal running the verification commands. Its password must match `.env`:

```sh
export ECOMMERCE_SQLSERVER='Server=127.0.0.1,14333;Database=Ecommerce;User Id=sa;Password=YOUR_LOCAL_PASSWORD;Encrypt=True;TrustServerCertificate=True'
dotnet run -- --verify-sqlserver
```

The SQL Server runner checks concurrent checkout, same-key replay, competing payment and refund operations, and transaction rollback. It writes to a separate `EcommerceVerification` database rather than the application's `Ecommerce` database.

Pause the Compose API before the broker runners so its consumer cannot take their verification deliveries:

```sh
docker compose stop ecommerce
dotnet run -- --verify-rabbitmq
dotnet run -- --verify-product-sync
docker compose up -d --wait ecommerce
```

The RabbitMQ runner uses a temporary SQLite database with the real broker. It checks confirmed publication, duplicate delivery, retry, dead-letter handling, and broker recovery. The product-sync runner checks catalog/Outbox atomicity, version ordering, deduplication, search outage recovery, and the SQL Server → RabbitMQ → Elasticsearch path. It needs SQL Server, RabbitMQ, and Elasticsearch running.

CI also checks `/health` and waits for the sample products to appear through `/products/search`. The full authenticated HTTP flow is available as a [local demonstration](demo.md), but is not currently an automated end-to-end test.
