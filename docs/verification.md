# Verification

[Documentation index](README.md) · [Project overview](../README.md)

The solution has a small xUnit test project as well as custom infrastructure verification runners. Every database-backed runner uses SQL Server. Run the commands below from the repository root.

## Coverage

| Check | What it exercises | Dependencies |
| --- | --- | --- |
| `dotnet test Ecommerce.sln` | Order replay matching and payment totals from saved purchase prices | .NET 10 SDK; no Docker |
| `--verify` | Order/payment rules, idempotency, cancellation, expiration, refunds, Outbox, and rollback | SQL Server; disposable databases |
| `--verify-sqlserver` | Concurrent same-key orders, competing payment/refund operations, locking, and rollback | SQL Server; separate `EcommerceVerification` database |
| `--verify-rabbitmq` | Confirmed publication, duplicate delivery, retries, dead letters, and broker recovery | SQL Server and real RabbitMQ |
| `--verify-product-sync` | Catalog/Outbox atomicity, stock preservation, version ordering, deduplication, and search outage recovery | SQL Server, RabbitMQ, and Elasticsearch |
| [HTTP script](../scripts/verify_http.py) | Search, registration/login, checkout and payment-attempt replay/conflicts, authentication, and order ownership | Running API and its Compose dependencies |

The xUnit project contains focused unit tests. The first two custom runners are service/integration checks. Broker and product-sync checks also exercise real infrastructure. The HTTP script is a smoke/E2E check of one checkout path; payment outcomes and refunds are checked at service level because they have no public HTTP endpoints. There is no contract-test suite. General test categories are explained in the [testing reference](knowledge/reliability-observability-testing.md#testing).

## How the checks fit together

The checks exercise the same application at different boundaries; they do not call one another. `dotnet test` checks small C# rules without Docker. The `dotnet run -- --verify...` commands call services against disposable SQL Server databases and, for the focused runners, real RabbitMQ and Elasticsearch. `scripts/verify_http.py` acts as a client of the running API: it sends HTTP requests and checks responses rather than calling C# services directly.

For example, an order request that reuses an idempotency key with a changed quantity is checked at three levels: the unit test rejects the item match, the SQL Server verification checks the saved order and stock, and the HTTP script checks the conflict returned to the client. This overlap catches wiring or persistence mistakes that a rule-only test cannot see.

The [GitHub Actions workflow](../.github/workflows/ecommerce.yml) is the single orchestrator: it builds, runs `dotnet test`, starts the Compose stack, and runs the remaining checks in sequence. One failed step fails the workflow. Locally, `dotnet test Ecommerce.sln` runs only the xUnit tests; use the commands below for the infrastructure and HTTP checks.

## Fast tests

```sh
dotnet test Ecommerce.sln
```

These tests do not start the application or its containers. They check that an idempotent order replay accepts the same items regardless of order, rejects changed details, and that payment amounts use saved order-item prices and long arithmetic. They do not replace the SQL Server concurrency or HTTP checks below.

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

1. Restore and build the solution, then run the xUnit tests with `dotnet test`.
2. Generate a temporary SQL Server password, start the Compose stack, and run `--verify`.
3. Check API health and wait for the seed products to appear in search.
4. Run the HTTP script and `--verify-sqlserver`.
5. Pause the API consumer, then run `--verify-rabbitmq` and `--verify-product-sync`.
6. Restart the API and check health again.
7. Show container logs on failure and always remove the CI stack and its volumes.

In GitHub, open **Actions → Ecommerce → the run for your commit**. A successful run means its required checks completed successfully. A failed run identifies the step to inspect; an unfinished, skipped, or cancelled run is not a passing result. Locally, inspect each command's result and exit status—building successfully alone does not run every check.

This workflow performs continuous integration (CI). It does not publish a container image or deploy an environment, so CD is not implemented. Passing checks provide evidence for the scenarios in the coverage table, not proof that every possible behavior is correct.
