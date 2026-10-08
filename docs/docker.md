# Docker and local operations

Run these commands from the repository root with the Docker engine running.
Start with the [project quick start](../README.md#run-with-docker-compose).

## What Docker runs

An image packages an application. A container runs that image. Compose starts
related containers together. A volume keeps data outside a container.

| Service | Purpose | Host port | Address inside Compose |
| --- | --- | --- | --- |
| `ecommerce` | API, storefront, and background workers | 5088 | `ecommerce:8080` |
| `sqlserver` | Accounts and commerce records | 14333 | `sqlserver:1433` |
| `rabbitmq` | Event delivery | 5672; management UI 15672 | `rabbitmq:5672` |
| `elasticsearch` | Product search | 9200 | `elasticsearch:9200` |
| `redis` | Search cache and temporary carts | 6379 | `redis:6379` |

Host ports bind to `127.0.0.1`. Inside a container, `localhost` means that
container; use service names to reach other services. The optional telemetry
dashboard uses host port 18888; see [metrics and tracing](observability.md).

## Everyday commands

| Command | Purpose |
| --- | --- |
| `docker info` | Check the Docker engine |
| `docker compose up -d --build --wait` | Build and start the stack |
| `docker compose up -d --build --wait ecommerce` | Rebuild the API and storefront |
| `docker compose ps -a` | Show this stack's containers |
| `docker ps -a` | Show all containers |
| `docker compose logs --tail=50 ecommerce` | Read recent API logs |
| `docker compose logs -f ecommerce` | Follow API logs |
| `docker compose stop` / `start` | Stop/start existing containers |
| `docker compose restart ecommerce` | Restart the existing API container |
| `docker compose down` | Remove containers and network, keeping named volumes |
| `docker compose images` | List stack images |
| `docker stats` | Monitor resource use |

Press Ctrl+C to leave live logs or monitoring. Code changes need a rebuild;
environment changes need `up` to recreate the container. `restart` does neither.
Keep any Development or observability overlay in your rebuild command.

## Read container state

| State | Meaning |
| --- | --- |
| `Up` | Process is running |
| `healthy` | Configured health check passed |
| `starting` | Initial health checks are still running |
| `unhealthy` | Health check is failing; inspect logs |
| `Exited (0)` | Process stopped successfully |
| Other `Exited` code or `Restarting` | Process failed or is restarting; inspect logs |

The API container checks SQL through `/health`. Compose waits for SQL, RabbitMQ,
and Elasticsearch before starting it. Redis has a PING health check but is not
an API startup dependency. Use [health reporting](health.md) to inspect all four services.
Repeated Outbox queries and successful health requests are normal background logs.

## Configuration boundaries

Compose reads `.env` and passes the settings declared in [compose.yaml](../compose.yaml).
`dotnet run` does not automatically read `.env`. For host startup, set environment
variables yourself. Double underscores represent nested configuration keys.

| Setting | Purpose |
| --- | --- |
| `MSSQL_SA_PASSWORD` | Compose SQL password and API connection string |
| `ConnectionStrings__ShopDatabase` | API SQL connection string |
| `Redis__ConnectionString` | Host default: `localhost:6379,abortConnect=false` |
| `Elasticsearch__Url` | Host default: `http://localhost:9200` |
| `RabbitMq__HostName` / `RabbitMq__Port` | Host defaults: `localhost` / `5672` |
| `RabbitMq__UserName` / `RabbitMq__Password` | Local defaults: `guest` / `guest` |
| `DEFAULT_ADMIN_ENABLED`, `DEFAULT_ADMIN_EMAIL`, `DEFAULT_ADMIN_PASSWORD` | Compose maps these to default-account settings; see [admin access](admin.md) |
| `Observability__OtlpEndpoint` | Optional telemetry export address |
| `ECOMMERCE_SQLSERVER` | Connection for verification runners and EF tooling |
| `ECOMMERCE_BASE_URL` | HTTP-check target, default `http://127.0.0.1:5088` |

API environment variables override [appsettings.json](../src/Ecommerce.Api/appsettings.json).
Adding an arbitrary name to `.env` does not pass it to the API container; add it
to the service environment or an overlay. See [rate-limit settings](rate-limiting.md).

## Run the API on the host

Use the .NET 10 SDK. Stop the Compose API to avoid two sets of workers, but keep
its dependencies running. Replace the password below with the one in `.env`:

```sh
docker compose stop ecommerce
docker compose up -d --wait sqlserver rabbitmq elasticsearch redis
export ConnectionStrings__ShopDatabase='Server=127.0.0.1,14333;Database=Ecommerce;User Id=sa;Password=YOUR_LOCAL_PASSWORD;Encrypt=True;TrustServerCertificate=True'
dotnet run --project src/Ecommerce.Api -- --urls http://127.0.0.1:5088
```

Use [Vite](../src/storefront/README.md) for frontend development. After stopping
the host API, restore Compose with `docker compose up -d --wait ecommerce`.

## Inspect the stack

```sh
curl -i http://127.0.0.1:5088/health/dependencies
docker compose exec redis redis-cli PING
docker compose exec rabbitmq rabbitmq-diagnostics -q ping
curl -s 'http://127.0.0.1:9200/_cluster/health?pretty'
docker compose logs --since=2m ecommerce | rg 'Search cache|Redis'
```

Expect healthy dependency labels, Redis `PONG`, and a successful broker ping.
Elasticsearch yellow is accepted on one node; red means primary shards are unavailable.
These checks report connectivity, not a complete checkout test.

To enter a container, use `docker compose exec ecommerce sh` or
`docker compose exec redis redis-cli`. Leave with `exit` or `QUIT` respectively.
`exec -T` runs without an interactive terminal, which suits scripts.

## Inspect event delivery

1. Confirm the write committed in SQL.
2. Inspect unpublished Outbox rows with a SQL client:

```sql
SELECT TOP (50) Id, OrderId, Type, PublishedAt, AttemptCount,
    NextAttemptAt, DeadLettered, LastError
FROM dbo.Outbox
WHERE PublishedAt IS NULL
ORDER BY NextAttemptAt, Id;
```

3. Check attempts, next retry, and errors. `PublishedAt` means broker publication
   completed; it does not mean the consumer finished.
4. Inspect broker queues:

```sh
docker compose exec -T rabbitmq rabbitmqctl list_queues name messages_ready messages_unacknowledged consumers
```

The main, retry, and dead queues are `ecommerce.events.consumer`,
`ecommerce.events.retry.consumer`, and `ecommerce.events.dead.consumer`.
For product events, check the consumer logs and search result too.

See [RabbitMQ delivery](rabbitmq.md) for retry rules. Dead-lettered rows/messages
need investigation; no replay CLI is implemented. Fix the cause before replaying
and retain the original event ID.

## Image build and data

[Dockerfile](../Dockerfile) has three stages: Node builds the storefront, the .NET
SDK publishes the API, and the ASP.NET runtime receives both outputs. The final
container runs `dotnet Ecommerce.Api.dll` as the image's application user.
[.dockerignore](../.dockerignore) excludes secrets and local build output.

SQL Server, RabbitMQ, and Elasticsearch have named volumes. Redis has no explicit
volume, so cache and cart data may be lost when its container is recreated.
Dashboard telemetry is also temporary. Restarts do not refill product stock.

`docker compose down -v` deletes named volumes, including accounts and orders.
Volumes are not backups; automated backup/restore is not implemented here.

## Troubleshooting

| Problem | First check |
| --- | --- |
| Docker unavailable | Docker engine and `docker context show` |
| Startup fails | API logs, SQL health, connection string, and default-admin settings |
| Port in use | Other containers or a host process using the mapped port |
| UI/code looks unchanged | Rebuild; confirm the new container started |
| API healthy, search failing | Elasticsearch, Redis, and API logs |
| Newly created product missing | Outbox, broker queues, consumer logs, then search refresh |
| 401 / 403 | Sign in / check permissions and obtain a fresh token |
| 409 on checkout | Read the stock or idempotency conflict message |
| Checkout test fails on seed stock | Repeated tests may have consumed the sample product |

Use [admin access](admin.md), [payment simulation](payments.md#development-simulation),
[Redis exercises](redis.md), and [verification](verification.md) for detailed steps.
