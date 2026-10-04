# Reliability, observability, and testing

[Learning index](README.md) · [Documentation index](../README.md)

Start with background execution, then learn how to recover from failures, observe behavior, and verify the system. Code snippets are general examples unless they link to a repository file.

> **In this project:** The app has background workers, structured `ILogger` calls, a SQL Server-only health endpoint, custom service/infrastructure runners, and an HTTP smoke/E2E script. It does not yet collect metrics or traces, expose full dependency readiness, or have a separate xUnit/contract-test suite. See [verification](../verification.md), [the HTTP script](../../scripts/verify_http.py), and [health endpoint](../../Endpoints/HealthEndpoints.cs).

## On this page

- [Background Jobs](#background-jobs)
- [Background Job Reliability](#background-job-reliability)
- [Resilience](#resilience)
- [Observability](#observability)
- [Health Checks](#health-checks)
- [Testing](#testing)
- [Project examples](#project-examples)

## Background Jobs

Useful for:

- Sending email
- Generating PDF files
- Cleanup
- Data export
- Large processing jobs

### BackgroundService

Good for simple in-process workers.

### Hangfire

Useful for:

- Persistent jobs
- Retry
- Dashboard
- Recurring jobs

### Quartz.NET

Strong scheduling framework for complex timing rules.

### Cron

A syntax for recurring schedules.

## Background Job Reliability

Important concerns:

- Durability
- Retry
- Idempotency
- Distributed lock
- Concurrency limits
- Graceful shutdown

A common long-running API pattern:

```text
POST /jobs
↓
Return JobId
↓
Worker processes job
↓
GET /jobs/{id}
```

This is often called:

**Asynchronous Job API Pattern**

## Resilience

### Timeout

External calls should have time limits.

### Retry

Useful for transient failures.

Examples:

- Temporary network failure
- Temporary service unavailable
- Timeout

Do not blindly retry non-idempotent operations.

### Exponential Backoff

Increase delay between retries.

### Jitter

Add randomness so many clients do not retry at the exact same time.

### Backpressure and retry budgets

If incoming work arrives faster than consumers finish it, the backlog grows. Limit in-flight work, monitor queue depth and message age, and fix the slow dependency before simply adding retries. A retry budget limits the extra work failures create; retries at several layers can otherwise multiply traffic.

Here the RabbitMQ worker sets prefetch to one, so one consumer has at most one unacknowledged delivery at a time. That does not cap the queue's total size. The retry queue delays messages, while failures classified as infrastructure outages remain retryable without a fixed attempt limit. Metrics and backlog alerts are future operational work.

### Circuit Breaker

Typical states:

```text
Closed
Open
Half-Open
```

Prevents repeated calls to an unhealthy dependency.

### Bulkhead

Isolates resource pools so one failing dependency does not consume all resources.

### Fallback

Provide a reduced or alternative result.

### Graceful Degradation

Some features become unavailable while core functionality remains usable.

## Observability

Three core pillars:

```text
Logs
Metrics
Traces
```

### Logging

Prefer:

**Structured Logging**

Useful fields:

- RequestId
- TraceId
- OrderId
- Error type
- Duration

Never log:

- Passwords
- Access tokens
- API secrets
- Sensitive personal data

### Metrics

Examples:

- Request rate
- Error rate
- Latency
- CPU
- Memory
- Queue length
- Database connection pool usage

### Tracing

Example:

```text
API
↓
Order Service
↓
Payment Service
↓
Database
```

Tracing connects all steps into one distributed request path.

Important concepts:

- TraceId
- SpanId
- CorrelationId

Common standard:

**OpenTelemetry**

## Health Checks

### Liveness

Question:

> Is the process alive?

Failure may trigger restart.

### Readiness

Question:

> Can this instance safely receive traffic?

If not ready, the load balancer should stop sending new requests.

## Testing

### Unit Test

Tests an isolated function or class.

Common tools:

- xUnit
- NUnit
- MSTest
- Moq
- NSubstitute

### Integration Test

Tests real component interactions:

- Database
- Redis
- HTTP
- Message Broker

Testcontainers is useful for spinning up temporary real dependencies.

### Contract Test

Verifies service-to-service API or message contracts.

### End-to-End Test

Tests the complete workflow from the user entry point.

### API tests, smoke checks, and regression checks

These labels describe different dimensions. An API test uses an API boundary; it may use an in-memory server or a deployed service. A smoke check tests a small set of essential paths. A regression check preserves behavior that previously failed. An E2E test crosses the real system for a selected journey. One test can fit several labels.

For example, this project's HTTP script is both an API check and a smoke/E2E checkout check. The custom verification runners are integration checks even though they are launched with `dotnet run` instead of `dotnet test`.

### Test Pyramid

Typical balance:

```text
Many Unit Tests
↓
Fewer Integration Tests
↓
Few End-to-End Tests
```

Important areas to test:

- Business rules
- Concurrency
- Transactions
- Outbox
- Idempotency
- Authorization
- Failure scenarios

## Project examples

The background workers use `ILogger` for failures and connection events. [`GET /health`](../../Endpoints/HealthEndpoints.cs) checks only SQL Server connectivity, so `200` does not prove RabbitMQ or Elasticsearch is healthy. The repository does not yet collect application metrics, distributed traces, or full readiness signals. There is no circuit breaker or cloud secrets service configured.

See the [verification guide](../verification.md) for the current coverage matrix, commands, HTTP smoke/E2E path, and CI result. The general test categories in this chapter do not imply that every category has a suite in this repository.

---

Previous: [Search and system design](search-and-system-design.md) · [Learning index](README.md) · Next: [Deployment, scaling, and infrastructure](deployment-scaling-infrastructure.md)
