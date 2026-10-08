# E-commerce application

A learning project built with ASP.NET Core (.NET 10) and React + TypeScript.
It includes a storefront, an API, and local Docker services.

## What it does

- Browse, search, and sort products; view product details.
- Register, sign in, manage a cart, and check out with stock checks.
- Create orders and payment attempts, cancel eligible orders, and request refunds or returns.
- Manage products, shipments, and returns; read orders and payments across customers.
- Record metrics and traces with OpenTelemetry.

Payments and refunds are simulated. The application does not charge real money.

## Run with Docker Compose

Run commands from the repository root. You need Docker with Compose.

For the first run, copy the settings file:

```sh
cp -n .env.example .env
```

Open `.env` and set `MSSQL_SA_PASSWORD` to a local password with at least eight
characters, including uppercase, lowercase, a number, and a symbol. Then start:

```sh
docker compose up -d --build --wait
```

Open [the storefront](http://127.0.0.1:5088/). The API uses the same address.
Startup applies database migrations and adds two sample products if the catalog
is empty. Search indexing may take a few seconds.

Check the stack:

```sh
docker compose ps
curl http://127.0.0.1:5088/health/dependencies
docker compose logs --tail=50 ecommerce
```

Stop it with `docker compose down`. SQL Server, RabbitMQ, and Elasticsearch data
remain in Docker volumes. `docker compose down -v` also deletes those volumes.

See the [Docker guide](docs/docker.md) for service ports and basic commands, or
[Docker operations](docs/docker.md) to run the API on your host.

## Admin access

To enable an account with all five current admin permissions, set
`DEFAULT_ADMIN_ENABLED`, `DEFAULT_ADMIN_EMAIL`, and `DEFAULT_ADMIN_PASSWORD` in
`.env`, then recreate the API. Follow the [admin guide](docs/admin.md).
Existing account passwords are preserved on restart.

The storefront has Products admin, Orders admin, and Payments admin pages.
Shipment and return administration uses the API.

## Catalog and search

SQL Server stores products, orders, payments, and inventory. RabbitMQ delivers
saved events to update Elasticsearch. Redis caches search results and stores
temporary carts. Checkout always checks current SQL prices and stock.

Add products through Products admin or the [catalog CLI](docs/product-sync.md).
See [Redis exercises](docs/redis.md) to inspect cache keys and expiration.

## Project layout

| Folder | Contents |
| --- | --- |
| [src/Ecommerce.Api](src/Ecommerce.Api/README.md) | API controllers, business services, and infrastructure |
| [src/storefront](src/storefront/README.md) | React interface |
| [tests](tests/README.md) | Backend tests and HTTP checks |
| [requests](requests/README.md) | Manual HTTP examples |
| [tools/demo](tools/demo/README.md) | Optional demo-data maintenance |
| [docs](docs/README.md) | Detailed guides and learning notes |

The API groups controllers and services by feature. See [project structure](docs/project-structure.md).

## Tests

With the .NET 10 SDK installed:

```sh
dotnet test Ecommerce.sln
```

For frontend and running-stack checks, see the [test guide](tests/README.md).

## Metrics and tracing

Enable the optional dashboard using the [observability guide](docs/observability.md).
It explains login, metrics, traces, and temporary telemetry storage.

## Current limitations

- Payment, refund, and carrier integrations are local simulations or unfinished.
- Background workers assume one API instance; rate-limit counters are per process.
- Redis carts are temporary, and dashboard telemetry has no persistent storage.
- Search updates asynchronously; direct SQL edits bypass catalog synchronization.
- Returns do not automatically restock products. User management and unrestricted order/payment edits are not implemented.
- Compose is for local development. Production deployment and continuous delivery are not configured.

See the [DI, database, and Redis review](docs/architecture-review.md) for known
correctness issues and suggested improvements. These recommendations are not yet implemented.

Start with the [documentation index](docs/README.md) for more detail.
