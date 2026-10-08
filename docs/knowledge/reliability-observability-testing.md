# Reliability, observability, and testing

[Learning index](README.md) · [Documentation index](../README.md)

Learn how background work recovers from failure, how to see what the app is doing, and how different checks verify its behavior.

**In this project:** Hosted workers handle expiration and event delivery. The app has structured logs, OpenTelemetry metrics and traces, liveness and dependency health endpoints, xUnit tests, SQL/broker runners, HTTP checks, and storefront tests. See [verification](../verification.md), [health](../health.md), and [observability](../observability.md).

## On this page

- [Background Jobs](#background-jobs)
- [Background Job Reliability](#background-job-reliability)
- [Resilience](#resilience)
- [Observability](#observability)
- [Health Checks](#health-checks)
- [Testing](#testing)
- [Project examples](#project-examples)

## Background Jobs

Background jobs suit work such as email, PDFs, cleanup, exports, or processing that should outlive an HTTP request. An in-memory task alone is lost when its process stops; store important pending work durably.

### BackgroundService

ASP.NET Core's `BackgroundService` runs work inside the application process. It receives a stopping token for shutdown. This project uses it for order expiration, Outbox publication, and RabbitMQ consumption. The SQL Outbox and broker retain pending events across restarts.

### Hangfire

Hangfire provides persisted jobs, retries, recurring work, and a dashboard. It is an option when those job-management features are useful; it is not installed here.

### Quartz.NET

Quartz.NET supports detailed scheduling rules. Scheduling and reliable business effects are different concerns: a scheduled operation still needs appropriate persistence and duplicate protection.

### Cron

Cron expressions describe recurring schedules. Check the scheduler's field format and time zone; implementations differ, and daylight-saving changes can affect local schedules.

## Background Job Reliability

Decide where unfinished work lives, which failures are retryable, how duplicates behave, and how much concurrent work is allowed. Coordinate multiple workers when necessary and stop gracefully so uncompleted work can be recovered.

A common asynchronous job API returns an ID immediately:

```text
POST /jobs → save pending job → return JobId
Worker → process job → save result
GET /jobs/{id} → read progress or result
```

This pattern is a teaching example. It separates accepting work from completing it, and needs states for success, failure, and cancellation.

## Resilience

### Timeout

Set time limits for external calls so a slow dependency cannot hold resources forever. A timeout says the outcome is uncertain; it does not prove that an order, charge, or message was never saved.

### Retry

Retry failures that may be temporary, such as a connection failure or unavailable service. Keep the same logical operation identity. Do not retry a non-idempotent effect blindly or retry invalid input that cannot succeed unchanged.

### Exponential Backoff

Increase delay between attempts, usually up to a cap. For example, delays might grow from 2 to 4 to 8 seconds. This gives a recovering service time to recover.

### Jitter

Add randomness to retry timing so many clients do not return at once. The current SQL Outbox uses capped exponential delay; it does not add jitter.

### Backpressure and retry budgets

When work arrives faster than it finishes, the backlog grows. Limit in-flight work, watch message age and queue depth, and address the slow dependency. A retry budget limits how much extra traffic failures create. Retries at several layers can multiply the load.

The RabbitMQ worker uses prefetch one, allowing one unacknowledged delivery per consumer. That does not cap total queue size. Its delayed retry queue and infrastructure-failure classification are described in [RabbitMQ delivery](../rabbitmq.md). The app records messaging outcomes; production backlog alerts and capacity planning remain operational work.

### Circuit Breaker

A circuit breaker stops repeated calls to a failing dependency. Closed means normal calls proceed. Open means calls fail quickly. Half-open allows trial calls to see whether the dependency recovered. No circuit breaker is configured here.

### Bulkhead

A bulkhead gives work separate resource limits so one failing dependency cannot consume every thread, connection, or worker slot.

### Fallback

A fallback supplies an alternative result when the primary path fails. It must fit the business rule: an older recommendation may be acceptable, but an invented payment success is not.

### Graceful Degradation

Keep useful functionality available during a partial outage. Here search can bypass an unavailable Redis cache and still query Elasticsearch. Cart operations depend on Redis and can fail separately. A working SQL health check does not establish that search or carts work.

## Observability

Logs explain events, metrics summarize trends, and traces connect the steps of an operation. Use all three to answer what failed, how often it failed, and where time was spent.

### Logging

Structured logs keep fields separate from message text:

```csharp
logger.LogInformation("Order {OrderId} created", order.Id);
```

This teaching example makes the ID searchable without building a string manually. Useful fields include operation, result, duration, and trace/span IDs. Keep passwords, tokens, secrets, and unnecessary personal data out of logs.

### Metrics

Metrics measure rates, counts, durations, and resource use. Common examples are request volume, errors, latency, CPU, memory, queue depth, and pool usage.

The project's [CommerceTelemetry](../../src/Ecommerce.Api/Observability/CommerceTelemetry.cs) records search cache hits/misses/errors, operation outcomes and durations, and messaging outcomes. OpenTelemetry also collects API and runtime metrics. Labels use bounded categories; putting each customer or order ID into labels would create too many separate series.

Not every example metric above is implemented. In particular, application outcome counters do not replace broker backlog measurements or production alerts.

### Tracing

A trace contains spans for work such as an HTTP request, SQL query, or event publication. A trace ID connects the spans; each span has its own ID. W3C trace context passes that relationship across process and message boundaries.

The app uses OpenTelemetry for requests, outgoing HTTP, SQL, and commerce operations. Outbox rows preserve validated trace context, and RabbitMQ producer/consumer spans continue it after the request ends. Event IDs and business IDs still serve different purposes.

Telemetry export is optional. Set an OTLP endpoint or use the local viewer in the [observability guide](../observability.md). Without an endpoint the app does not export telemetry. The current setup does not provide production retention or alerting.

## Health Checks

### Liveness

Liveness asks whether the process is running well enough to answer a basic probe. An orchestrator may restart a process after repeated failures. Dependency outages should not automatically cause every healthy process to restart.

`GET /health/live` returns a process-only response here.

### Readiness

Readiness asks whether this instance should receive traffic for its intended role. If it is not ready, a load balancer should stop routing new work to it.

`GET /health/dependencies` checks SQL Server, RabbitMQ, Elasticsearch, and Redis and returns `503` when a check is unhealthy. `GET /health` retains the older SQL-connectivity check. These probes report their checks at that time; they do not prove that a queue is drained, every workflow succeeds, or a production rollout is safe. See [HealthController](../../src/Ecommerce.Api/Common/Controllers/HealthController.cs).

## Testing

### Unit Test

A unit test checks a small function or class with controlled inputs. xUnit, NUnit, and MSTest are common frameworks; Moq and NSubstitute can replace collaborators when that helps.

This project has an [xUnit suite](../../tests/Ecommerce.Api.Tests/Ecommerce.Api.Tests.csproj) for rules, parameters, response mappings, administration, and telemetry. The storefront also has Vitest tests.

### Integration Test

An integration test checks real interactions with a database, cache, broker, or server. Testcontainers can create temporary dependencies; this repository instead uses SQL verification databases and local Compose services.

SQL Server-specific locks need SQL Server checks. An in-memory fake cannot establish their concurrency behavior.

### Contract Test

A contract test checks the API or event shape agreed between a producer and consumer. HTTP response checks and DTO tests cover parts of contracts here, but there is no separate consumer-driven contract-test system.

### End-to-End Test

An end-to-end test crosses the system from a user's entry point for a selected journey. The Playwright storefront tests cover browser workflows, while HTTP scripts exercise API workflows.

### API tests, smoke checks, and regression checks

These names describe different things. API means the entry boundary. Smoke means a small set of essential paths. Regression means preserving a behavior that previously failed. End-to-end means crossing the real system for a selected journey. One check can fit several labels.

The [checkout HTTP script](../../tests/http/verify_checkout_http.py) is an API smoke check across the running stack. Custom runners launched with `dotnet run` are integration checks even though they do not use `dotnet test`.

### Test Pyramid

A typical test pyramid has many fast checks, fewer integration checks, and a small number of broad journeys. Use it to keep feedback useful and failures understandable, rather than as a fixed count rule.

Cover business rules, authorization, replay, concurrent writes, transactions, Outbox recovery, and failure paths. Include payment/refund state, shipment history, and returns where they affect the workflow.

## Project examples

Start with the [verification guide](../verification.md) to choose checks and understand what they create. The SQL concurrency runner retains its verification database rows; several other runners create and delete temporary databases. HTTP and browser checks can leave fixtures in the local app database.

Inspect [OutboxWorker](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxWorker.cs) for scoped work and shutdown. Inspect [TelemetryRegistration](../../src/Ecommerce.Api/Observability/TelemetryRegistration.cs) for instrumentation and optional export, then follow an event from request to publication and consumption in the viewer.

Compare the three health endpoints during a dependency outage. SQL connectivity, process liveness, and dependency checks answer different questions. A production deployment still needs appropriate routing, alerting, and recovery policy around those signals.

---

Previous: [Search and system design](search-and-system-design.md) · [Learning index](README.md) · Next: [Deployment, scaling, and infrastructure](deployment-scaling-infrastructure.md)
