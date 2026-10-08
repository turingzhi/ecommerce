# Backend learning guide

Seven chapters explain backend concepts with examples from this project.
Read them in order, or start with the topic you are working on.

## Reading order

1. [C# fundamentals](csharp-fundamentals.md): types, methods, interfaces, DI, LINQ, and async.
2. [HTTP, ASP.NET Core, and security](http-apis-security.md): requests, middleware, configuration, and authorization.
3. [Data and concurrency](data-concurrency.md): EF Core, indexes, transactions, locks, and migrations.
4. [Caching and messaging](cache-messaging-consistency.md): Redis, queues, idempotency, and the Outbox.
5. [Search and system design](search-and-system-design.md): indexes, request flows, and trade-offs.
6. [Reliability and testing](reliability-observability-testing.md): retries, logs, metrics, traces, and tests.
7. [Deployment and scaling](deployment-scaling-infrastructure.md): Docker, CI/CD, networking, and infrastructure.

## DI quick links

- [Service lifetimes](csharp-fundamentals.md#service-lifetimes): singleton, scoped, and transient reuse.
- [Request lifecycle](csharp-fundamentals.md#http-request-lifecycle): resolution and scope cleanup.
- [Disposal and garbage collection](csharp-fundamentals.md#disposal-versus-garbage-collection): resources, memory, and ownership.
- [DbContext and Redis](csharp-fundamentals.md#dbcontext-versus-redis): why their lifetimes differ.
- [Multiple implementations](csharp-fundamentals.md#multiple-implementations-of-one-interface): collections and keyed services.

## Learn with the project

Follow one request from its [endpoint to its service](../project-structure.md).
Then read the matching [workflow](../workflows.md), run the [local demo](../demo.md),
and inspect the [Redis keys](../redis.md) or [metrics and traces](../observability.md).
Use the [verification guide](../verification.md) to check the behavior.

The chapters also cover ideas beyond the installed application, including Kafka,
sagas, sharding, Kubernetes, and cloud deployment. See the
[project limitations](../../README.md#current-limitations) for the current scope.

Return to the [documentation index](../README.md) for setup and usage instructions.
