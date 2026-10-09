# Documentation

Start with the [project README](../README.md) to run the application.
Commands run from the repository root unless a guide says otherwise.

## Run and use

- [Docker and local operations](docker.md): setup, ports, configuration, logs, and troubleshooting.
- [Storefront](storefront.md): browsing, sign-in, checkout, and admin pages.
- [Admin access](admin.md): default account and individual permissions.
- [API reference](api.md): routes, request bodies, responses, and errors.
- [Local demo](demo.md): checkout and search outage exercises.
- [Verification](verification.md): unit, SQL, HTTP, browser, and CI checks.
- [Health](health.md): liveness and dependency reports.
- [Metrics and tracing](observability.md): OpenTelemetry and dashboard login.

## Business features

- [Cart](cart.md): quantities, expiration, and checkout.
- [Payments and refunds](payments.md): attempts, balances, and local simulation.
- [Rate limiting](rate-limiting.md): shared quotas and retry behavior.

## Understand the code

- [Project structure](project-structure.md): folders, names, and where to find endpoints/services.
- [Architecture](architecture.md): components and request flow.
- [Dependency injection](knowledge/csharp-fundamentals.md#interfaces-and-dependency-injection): lifetimes, request scopes, disposal, and interfaces.
- [DI, database, and Redis review](architecture-review.md): verified findings, risks, and a prioritized plan.
- [Workflows](workflows.md): transactions, state changes, and duplicate requests.
- [RabbitMQ](rabbitmq.md): Outbox publication, retries, and duplicate delivery.
- [Product synchronization](product-sync.md): catalog writes and Elasticsearch indexing.
- [Redis](redis.md): cache keys, expiration, invalidation, and CLI exercises.
- [Order-list measurement](sqlserver-order-list-performance.md): one dated SQL index comparison.
- [Backend learning guide](knowledge/README.md): seven chapters with project examples.
- [Feature history](history/2026-10-08-features.md): earlier design decisions.

Each topic has one main guide. Link to it instead of copying the same explanation.
