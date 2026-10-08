# Metrics and distributed traces

[Documentation index](README.md) · [Project overview](../README.md)

The API uses OpenTelemetry to record request/runtime metrics and traces through HTTP, SQL, Redis, Elasticsearch, and messaging work. Export is optional. Console logs remain available through `docker compose logs ecommerce`; this setup does not export logs to the dashboard.

## What is recorded

Metrics summarize counts and durations. Traces connect spans for one request and its later event processing.

| Custom metric | Meaning |
| --- | --- |
| `ecommerce.search.cache.requests` | Cache hit, miss, and read-error counts |
| `ecommerce.operation.outcomes` | Commerce operation outcomes |
| `ecommerce.operation.duration` | Operation duration in seconds |
| `ecommerce.messaging.outcomes` | Publish/consume outcomes, including replay, retry, and dead letters |

The API also instruments ASP.NET requests, runtime metrics, outbound HTTP spans, and SQL Client spans. Custom labels use fixed operation/result/dependency/event-type sets; unknown values become `other`. A Redis read failure records both `error` and the following fallback `miss`; write failures are logged. `/health` and `/assets` paths are excluded from request tracing.

Trace sanitization removes attributes outside a safe allowlist. Raw query text, bodies, IDs, email, bearer tokens, idempotency keys, SQL statements, and SQL parameters are not exported as attributes. SQL span names are generic, SQL parameter capture is disabled, and automatic exception events are disabled. Console logs follow their own logging behavior.

Sources: [TelemetryRegistration](../src/Ecommerce.Api/Observability/TelemetryRegistration.cs), [CommerceTelemetry](../src/Ecommerce.Api/Observability/CommerceTelemetry.cs), and [TelemetrySanitizer](../src/Ecommerce.Api/Observability/TelemetrySanitizer.cs).

## Optional local viewer

From the repository root:

```sh
docker compose -f compose.yaml -f compose.observability.yaml up -d --wait
docker compose -f compose.yaml -f compose.observability.yaml logs dashboard
```

Open [localhost:18888](http://127.0.0.1:18888). Use the dashboard login URL/token printed in its logs and keep it private. Browser authentication is enabled. The overlay pins Aspire dashboard 13.6.0 and sends OTLP internally to `http://dashboard:18889`; port 18889 is not published to the host.

Search twice to see miss/hit counts, then use cart, checkout, catalog admin, and payment requests to inspect operation outcomes/durations. The dashboard's Metrics and Traces views show exported telemetry. Trace links can connect HTTP work to delayed Outbox publication and consumption.

`Observability__OtlpEndpoint` enables export only for an absolute HTTP/HTTPS URL. Without it, no exporter is registered. The exporter timeout is three seconds, and the dashboard is not a startup/readiness dependency.

To return to the default stack without deleting commerce data:

```sh
docker compose up -d --wait ecommerce
docker compose -f compose.yaml -f compose.observability.yaml stop dashboard
docker compose -f compose.yaml -f compose.observability.yaml rm -f dashboard
```

## Tracing through the Outbox

When SQL saves a new Outbox row, `ShopDbContext` captures validated W3C `TraceParent`/`TraceState` from the active span. Retries retain that context. Publication starts a producer span from it; RabbitMQ envelopes carry the producer context, and consumption starts a child consumer span. Older four-field envelopes remain valid. Missing or invalid context does not block processing, and baggage is not propagated.

Console logging scopes include TraceId/SpanId for correlation. SQL stores the Outbox's small context fields, which allow later work to join the trace; it does not store full traces or metrics. See [OutboxTraceCapture](../src/Ecommerce.Api/Observability/OutboxTraceCapture.cs) and [RabbitMQ delivery](rabbitmq.md).

## Storage and limits

This Compose overlay configures no dashboard persistence or storage volume, so telemetry history is lost when the dashboard shuts down. The [Aspire standalone dashboard guide](https://aspire.dev/dashboard/standalone/#enable-persistence) explains persistence options. The repository has no production telemetry retention, alerting, fleet coordination, or public Prometheus endpoint. Console/container logs have a separate lifetime from dashboard telemetry.

## Verify

`dotnet test tests/Ecommerce.Api.Tests` checks bounded labels and trace sanitization. `--verify-telemetry` checks SQL context capture and producer/consumer parent relationships; `--verify-rabbitmq` checks delayed correlation through the real broker. Pause the API consumer before broker verification. Disposable verification databases are removed afterward; browser/HTTP fixture data stays in the application database. See [verification](verification.md) for setup and commands.
