# Verification

[Documentation index](README.md) · [Project overview](../README.md)

The repository uses custom executable runners rather than a separate `dotnet test` project. Every database-backed runner uses SQL Server. Run the commands below from the repository root.

## Coverage

| Check | What it exercises | Dependencies |
| --- | --- | --- |
| `--verify` | Order/payment rules, idempotency, cancellation, expiration, refunds, Outbox, and rollback | SQL Server; disposable databases |
| `--verify-sqlserver` | Concurrent same-key orders, competing payment/refund operations, locking, and rollback | SQL Server; separate `EcommerceVerification` database |
| `--verify-rabbitmq` | Confirmed publication, duplicate delivery, retries, dead letters, and broker recovery | SQL Server and real RabbitMQ |
| `--verify-product-sync` | Catalog/Outbox atomicity, stock preservation, version ordering, deduplication, and search outage recovery | SQL Server, RabbitMQ, and Elasticsearch |
| [HTTP script](../scripts/verify_http.py) | Search, registration/login, checkout and payment-attempt replay/conflicts, authentication, and order ownership | Running API and its Compose dependencies |

The first two runners are service/integration checks. Broker and product-sync checks also exercise real infrastructure. The HTTP script is a smoke/E2E check of one checkout path; payment outcomes and refunds are checked at service level because they have no public HTTP endpoints. There is no separate isolated unit-test or contract-test suite. General test categories are explained in the [testing reference](knowledge/reliability-observability-testing.md#testing).

## SQL Server and infrastructure checks

Copy `.env.example` to `.env`, set `MSSQL_SA_PASSWORD`, and start the stack:

```sh
cp -n .env.example .env
# Set MSSQL_SA_PASSWORD in .env.
docker compose up -d --build --wait
```

Set `ECOMMERCE_SQLSERVER` in the terminal running verification. Its password must match `.env`:

```sh
export ECOMMERCE_SQLSERVER='Server=127.0.0.1,14333;Database=Ecommerce;User Id=sa;Password=YOUR_LOCAL_PASSWORD;Encrypt=True;TrustServerCertificate=True'
dotnet run -- --verify
dotnet run -- --verify-sqlserver
```

These runners write verification data outside the application's `Ecommerce` database. The focused SQL Server runner includes the [same-key lock sequence](workflows.md#two-requests-with-the-same-idempotency-key).

Pause the Compose API before the broker runners so its consumer cannot take their verification deliveries. Keep SQL Server, RabbitMQ, and Elasticsearch running:

```sh
docker compose stop ecommerce
dotnet run -- --verify-rabbitmq
dotnet run -- --verify-product-sync
docker compose up -d --wait ecommerce
```

Both broker runners use temporary SQL Server databases and remove them when finished. Restart the API afterward even if a check fails.

## HTTP smoke/E2E check

With the API running:

```sh
python3 scripts/verify_http.py
```

The script creates fresh test accounts and reserves one unit of the seeded Wireless Mouse each run. It therefore changes the local application's data and needs available seed stock. It checks responses over real HTTP, including replay with the same key, conflict with changed details, and another customer's inability to read the order. See the [local demonstration](demo.md) for a manual walkthrough and Elasticsearch recovery example.

## CI workflow and pass/fail results

On pushes and pull requests, the [Ecommerce workflow](../.github/workflows/ecommerce.yml) runs this sequence:

1. Restore and build the application; generate a temporary SQL Server password.
2. Start the Compose stack and run `--verify`.
3. Check API health and wait for the seed products to appear in search.
4. Run the HTTP script and `--verify-sqlserver`.
5. Pause the API consumer, then run `--verify-rabbitmq` and `--verify-product-sync`.
6. Restart the API and check health again.
7. Show container logs on failure and always remove the CI stack and its volumes.

In GitHub, open **Actions → Ecommerce → the run for your commit**. A successful run means its required checks completed successfully. A failed run identifies the step to inspect; an unfinished, skipped, or cancelled run is not a passing result. Locally, inspect each command's result and exit status—building successfully alone does not run every check.

This workflow performs continuous integration (CI). It does not publish a container image or deploy an environment, so CD is not implemented. Passing checks provide evidence for the scenarios in the coverage table, not proof that every possible behavior is correct.
