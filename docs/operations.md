# Local configuration and troubleshooting

[Documentation index](README.md) · [Project overview](../README.md)

Use the [quick start](../README.md#run-with-docker-compose) to start the local stack. This guide explains configuration, logs, data retention, and how to locate a stalled workflow. Commands run from the repository root.

## Configuration boundaries

Docker Compose reads `.env` to substitute `MSSQL_SA_PASSWORD` into the container configuration. Running `dotnet run` directly does not load that file through the current application code. Supply application configuration separately when running on the host.

| Setting | Used by | Purpose |
| --- | --- | --- |
| `MSSQL_SA_PASSWORD` | Compose | Password for local SQL Server and the API's composed connection string |
| `ConnectionStrings__ShopDatabase` | Application | SQL Server connection string; required for normal startup and catalog CLI commands |
| `Elasticsearch__Url` | Application | Search address; defaults to `http://localhost:9200` outside Compose |
| `RabbitMq__HostName` / `RabbitMq__Port` | Application | Broker address; defaults to `localhost:5672` |
| `RabbitMq__UserName` / `RabbitMq__Password` | Application | Broker credentials; local defaults are `guest` / `guest` |
| `ECOMMERCE_SQLSERVER` | Verification runners | SQL Server connection used to create verification databases; it does not configure the normal API |
| `ECOMMERCE_BASE_URL` | HTTP verification script | API address; defaults to `http://127.0.0.1:5088` |

Double underscores map to nested ASP.NET Core configuration keys. Environment variables override `appsettings.json` under the default configuration setup. See [ASP.NET Core configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0), [Program.cs](../Program.cs), [RabbitMqOptions](../Services/RabbitMqOptions.cs), and [Compose](../compose.yaml).

Inside Compose, the API reaches `sqlserver:1433`, `rabbitmq:5672`, and `elasticsearch:9200`. From the host, use `127.0.0.1:14333`, `127.0.0.1:5672`, and `127.0.0.1:9200`. Inside a container, `localhost` refers to that container.

To run the API on the host, stop the Compose API so two sets of workers do not compete, and keep the dependencies running:

```sh
docker compose stop ecommerce
docker compose up -d --wait sqlserver rabbitmq elasticsearch
export ConnectionStrings__ShopDatabase='Server=127.0.0.1,14333;Database=Ecommerce;User Id=sa;Password=YOUR_LOCAL_PASSWORD;Encrypt=True;TrustServerCertificate=True'
dotnet run -- --urls http://127.0.0.1:5088
```

Use the same local password as `.env`. After stopping the host process, restore the Compose API with `docker compose up -d --wait ecommerce`.

## Inspect the stack

```sh
docker info
docker compose ps
docker compose logs --tail=100 ecommerce sqlserver rabbitmq elasticsearch
curl -i http://127.0.0.1:5088/health
curl -i 'http://127.0.0.1:5088/products/search?q=wireless'
```

`docker info` checks access to the Docker engine. Container status and logs identify startup or connection failures. `/health` only checks SQL Server; it does not establish that messaging or search is working. Application workers emit `ILogger` messages into the API logs. Review logs before sharing them because exception details can contain local configuration or data.

| Symptom | What to check |
| --- | --- |
| Docker daemon cannot be reached | Check Docker Desktop's engine and active Docker context before debugging application code |
| API fails during startup | Check SQL Server health and the connection string; startup applies migrations before serving HTTP |
| Compose reports a port is already allocated | Check for an existing host API or another container using the mapped port |
| Health is `200`, search is `503` | Inspect Elasticsearch and the API search error; SQL health does not cover search |
| Search returns `[]` after product creation | Check the SQL Outbox and broker stages below; also allow for search refresh delay |
| Protected route returns `401` | Log in and use the returned bearer access token |
| Order creation returns `409` | Read the error: insufficient stock and conflicting key reuse need different fixes |
| HTTP verification cannot buy the seed product | The script expects the seeded Wireless Mouse at its original price with available stock; repeat runs consume stock |

## Inspect event delivery

1. Confirm the catalog or commerce write committed in SQL Server.
2. Inspect the corresponding Outbox row with a SQL client. This read-only query exposes delivery state without dumping event payloads:

   ```sql
   SELECT TOP (50)
       Id, OrderId, Type, PublishedAt, AttemptCount,
       LastAttemptAt, NextAttemptAt, DeadLettered, LastError
   FROM dbo.Outbox
   WHERE PublishedAt IS NULL
   ORDER BY NextAttemptAt, Id;
   ```

3. If publication is pending, check `NextAttemptAt`, `DeadLettered`, and API logs. The [delivery policy](rabbitmq.md#failure-classification-and-retry-timing) explains eligibility and failure classification. `PublishedAt` means publication completed, not that search indexing completed.
4. Inspect broker queues with this read-only command, or use the local management UI at `http://127.0.0.1:15672`:

   ```sh
   docker compose exec -T rabbitmq rabbitmqctl list_queues name messages_ready messages_unacknowledged consumers
   ```

   The main queue is `ecommerce.events.consumer`, the retry queue is `ecommerce.events.retry.consumer`, and the dead queue is `ecommerce.events.dead.consumer`. Names and bindings are defined in [RabbitMqTopology](../Services/RabbitMqTopology.cs).

5. For a product event, inspect consumer/search errors and retry the search. A low queue count alone does not prove the expected document is present.

Dead-lettered SQL rows and RabbitMQ dead-queue messages need investigation; this repository has no replay CLI or automatic dead-letter recovery. Preserve the original event ID when designing a replay. Diagnose and fix the cause first—clearing a flag or republishing a poison payload can repeat the failure. Ordinary recoverable outages follow the automatic retry path; use the [outage demonstration](demo.md#outage-and-recovery-elasticsearch-goes-offline) to observe it.

## Stop, retain, and recover data

`docker compose stop` stops containers; `docker compose down` removes containers and the Compose network. The named SQL Server, RabbitMQ, and Elasticsearch volumes survive both. Recreate the stack with `docker compose up -d --build --wait`.

`docker compose down -v` deletes those data volumes, including accounts, orders, catalog records, broker messages, and search documents. Use it only for an intentional disposable reset, not as the default response to an error. Startup creates sample products only when the catalog is empty; restarting does not replenish existing seed stock.

A retained volume is not a backup. There is no automated backup/restore workflow in this repository. SQL Server is authoritative; search is a derived projection and can be backfilled using the [catalog synchronization guide](product-sync.md). Recovering a lost SQL database requires a database backup. A future deployed environment needs a tested restore procedure and defined acceptable data loss and recovery time.
