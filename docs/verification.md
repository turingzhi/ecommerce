# Verification

Run commands from the repository root unless stated otherwise.
Use the .NET 10 SDK, Python 3, and Node 22 (22.12 or newer within that major version).

## How the checks fit together

| Check | Boundary | Needs |
| --- | --- | --- |
| Backend unit tests | C# rules, contracts, admin setup, telemetry, and controller HTTP behavior | .NET SDK |
| Frontend unit tests | UI state, API client, permissions, and retry behavior | Node and installed packages |
| SQL integration runners | Transactions, locks, schema constraints, and rollback | SQL Server |
| Broker runners | Publication, retries, deduplication, and indexing | SQL, RabbitMQ, and sometimes Elasticsearch |
| HTTP scripts | Requests against the real API | Running Compose stack |
| Browser checks | Storefront against the real API | Compose and Chromium |

Each layer checks different wiring. A successful build does not run these checks.

The [architecture review](architecture-review.md) inspected source without running
these application checks. It identifies missing boundary/concurrency cases and a
search verification race: a global generation change can belong to another event.
Wait for the expected updated result or its own processed marker when checking freshness.

## Fast tests

```sh
dotnet test Ecommerce.sln
npm --prefix src/storefront ci
npm --prefix src/storefront test
npm --prefix src/storefront run build
```

These do not need Docker. Node 25's experimental storage can conflict with jsdom;
use `NODE_OPTIONS=--no-experimental-webstorage` for the frontend test command on that version.

## SQL Server and infrastructure checks

Start the [Docker stack](docker.md) and set the verification connection string.
Replace the password with your local SQL password from `.env`:

```sh
export ECOMMERCE_SQLSERVER='Server=127.0.0.1,14333;Database=Ecommerce;User Id=sa;Password=YOUR_LOCAL_PASSWORD;Encrypt=True;TrustServerCertificate=True'
dotnet run --project src/Ecommerce.Api -- --verify
```

Most runners create and delete temporary GUID databases. `--verify-sqlserver` uses
the separate `EcommerceVerification` database and retains its test rows. They use
`ECOMMERCE_SQLSERVER`, not the API's `ConnectionStrings__ShopDatabase` setting.

| Flag | Coverage |
| --- | --- |
| `--verify` | Checkout, payment/refund rules, cancellation, expiration, Outbox, and rollback |
| `--verify-sqlserver` | Concurrent requests, lock behavior, and stock/payment/refund conflicts |
| `--verify-catalog` | Catalog versions, stock preservation, and write atomicity |
| `--verify-telemetry` | Safe telemetry and saved Outbox trace context |
| `--verify-rabbitmq` | Confirms, retries, dead letters, duplicates and trace correlation |
| `--verify-product-sync` | SQL → Outbox → RabbitMQ → Elasticsearch and outage recovery |

For shared broker checks, stop the API consumer so it cannot take test messages.
Keep the dependencies running:

```sh
docker compose stop ecommerce
dotnet run --project src/Ecommerce.Api -- --verify-rabbitmq
dotnet run --project src/Ecommerce.Api -- --verify-product-sync
docker compose up -d --wait ecommerce
```

Always restore the API after these checks, including after a failure.

## HTTP checks

Start the default Compose API, then run a script:

```sh
python3 tests/http/verify_catalog_admin_http.py
```

The table uses filenames under `tests/http`. Each script checks authentication and
relevant response/state rules as well as the behavior listed below.

| Script | Coverage | Extra mode |
| --- | --- | --- |
| `verify_checkout_http.py` | Checkout/payment replay and customer ownership | Requires available sample mouse stock |
| `verify_cancellation_http.py` | Repeat-safe cancellation and exact stock restoration | — |
| `verify_search_http.py` | Filters, sorting, totals, pagination, and cache freshness | — |
| `verify_catalog_admin_http.py` | Catalog permissions, writes, versions, and stock preservation | — |
| `verify_storefront_support_http.py` | Identity snapshot, UI config, and payment recovery | `--development` |
| `verify_admin_reads_http.py` | Independent read grants, cross-customer lists/details, and filters | — |
| `verify_default_admin_http.py` | Configured admin access and ordinary registration | `--restart` checks account/password preservation |
| `verify_cart_http.py` | Ownership, quantities, item cap, TTL, and checkout safety | `--outages` stops Redis temporarily |
| `verify_cart_checkout_http.py` | Cart checkout, current SQL values, concurrent replay, and retained cart | `--outages` |
| `verify_health_http.py` | Liveness and dependency reports | `--outages` stops each dependency in turn |
| `verify_payment_simulation_http.py` | Simulator access, outcomes, and repeat-safe events | Use `--disabled` against the default API |
| `verify_refund_http.py` | Refund validation, balances, and replay | `--development` for outcomes |
| `verify_financial_reads_http.py` | Payment/refund details, ownership, and history pagination | `--development` for populated history |
| `verify_rate_limit_http.py` | Independent quotas, 429, Retry-After, and recovery | Run last with default limits; waits about one minute |

HTTP and browser checks leave fresh accounts/products/orders/payments in the local
application database. Some checks reserve stock; startup does not refill it.
[Demo maintenance](../tools/demo/README.md) can refresh retained product names.

Development mode exercises simulated payment and refund outcomes.
Outage checks restore services in cleanup, but forced termination can prevent it;
run them on a local test stack and inspect `docker compose ps -a` afterward.

The default-admin check needs [setup enabled](admin.md#default-account).
If its setup password was cleared, supply the login password privately through
`ECOMMERCE_TEST_ADMIN_PASSWORD`. Checks do not print it.

## Development payment simulator checks

The default API has no payment/refund simulator routes. Check that first:

```sh
python3 tests/http/verify_payment_simulation_http.py --disabled
```

Enable the local simulator and test outcomes:

```sh
docker compose -f compose.yaml -f compose.development.yaml up -d --wait ecommerce
python3 tests/http/verify_payment_simulation_http.py
python3 tests/http/verify_refund_http.py --development
python3 tests/http/verify_financial_reads_http.py --development
```

Other scripts with `--development` in the table can use the same overlay.
Afterward, restore the default API with `docker compose up -d --wait ecommerce`.
See [payments and refunds](payments.md) for the state rules.

## Browser and telemetry checks

Install packages first. With Compose running, use these commands from `src/storefront`:

```sh
npx playwright install chromium
npm run test:e2e
```

With the Development overlay active, use `STOREFRONT_DEVELOPMENT=1 npm run test:e2e`.
Failures retain screenshots; browser traces are disabled to avoid recording bearer headers.

From the repository root:

```sh
npm --prefix src/storefront run test:observability
```

This check starts the dashboard, checks exported traces/metrics, tests a viewer
outage, then restores the default API and removes the dashboard container. It
retains application data and does not reset volumes. See [observability](observability.md).

## CI workflow and pass/fail results

[GitHub Actions](../.github/workflows/ecommerce.yml) installs dependencies, builds,
runs unit tests, starts Compose, and checks default HTTP/browser behavior.
It also checks the telemetry viewer, Development outcomes, SQL/broker workflows,
and final rate-limit recovery. Failure logs and browser screenshots help diagnosis.
The CI-only cleanup removes its test volumes.

Open **Actions → Ecommerce** to inspect a run. Failed, cancelled, unfinished, or
skipped checks are not evidence of success. CI checks the recorded scenarios; it
neither deploys the application nor proves every possible behavior correct.
