# Health reporting

Health checks tell you whether the API and its dependencies respond.
They are available without signing in.

## Choose the right endpoint

| Route | Check | Response |
| --- | --- | --- |
| `/health` | SQL connectivity | 200 if connected, otherwise 503 |
| `/health/live` | API responds to a request | 200 while responding |
| `/health/dependencies` | SQL, RabbitMQ, Elasticsearch, and Redis | 200 if all pass, otherwise 503 |

```sh
curl -i http://127.0.0.1:5088/health/live
curl -i http://127.0.0.1:5088/health/dependencies
```

A Redis failure can look like this:

```json
{
  "status": "unhealthy",
  "dependencies": {
    "sqlserver": "healthy",
    "rabbitmq": "healthy",
    "elasticsearch": "healthy",
    "redis": "unhealthy"
  }
}
```

The new routes use `Cache-Control: no-store` and return generic status labels.
They do not expose connection strings or exception details.

## What each check proves

- SQL: the configured database can be reached.
- RabbitMQ: an authenticated AMQP connection opens; no event is published.
- Elasticsearch: cluster health responds without timeout; yellow is accepted, red fails.
- Redis: PING completes on the API's shared connection; no key is changed.

The checks run concurrently with cancellation timeouts. Connection setup and
cleanup can exceed the requested timeout, so five seconds is not a strict deadline.
Code lives in [DependencyHealthChecks](../src/Ecommerce.Api/Infrastructure/Health/DependencyHealthChecks.cs)
and [HealthController](../src/Ecommerce.Api/Common/Controllers/HealthController.cs).

The Docker API check uses SQL-only `/health`. An aggregate 503 identifies a failed
dependency; it does not mean the API process should automatically restart.
Connectivity checks do not prove stock, payment, delivery, or search correctness.

## Test outage reporting

From the repository root with Compose running:

```sh
python3 tests/http/verify_health_http.py
```

On a local test stack, add `--outages` to stop each dependency and check recovery.
The script restores stopped services in `finally`; a forced process termination
can prevent that cleanup. Use `docker compose start` if needed.
See [verification](verification.md) for workflow checks.
