# Backend learning guide

A reading path from C# fundamentals to backend system design, connected to this e-commerce application. These seven chapters cover all **57 topics** from the supplied `csharp_dotnet_backend_core_knowledge_en.md` note, plus the existing C#, encapsulation, and DI material. Topics are grouped by subject, with repeated guidance consolidated and explanations expanded where needed.

## Reading order

| Order | Chapter | Focus |
| --- | --- | --- |
| 1 | [C# fundamentals, encapsulation, and DI](csharp-fundamentals.md) | Types, methods, inheritance, interfaces, DI lifetimes, LINQ, async, and thread pools |
| 2 | [HTTP, ASP.NET Core, and security](http-apis-security.md) | Requests, middleware, layering, configuration, identity, authorization, and security |
| 3 | [Data access and concurrency](data-concurrency.md) | EF Core, indexes, transactions, locking, connection pools, migrations, replicas, and sharding |
| 4 | [Caching, messaging, and consistency](cache-messaging-consistency.md) | Redis, queues, delivery semantics, idempotency, Outbox, sagas, and CAP |
| 5 | [Search and system design](search-and-system-design.md) | Search indexes, SQL versus search, request flows, trade-offs, and design questions |
| 6 | [Reliability, observability, and testing](reliability-observability-testing.md) | Background jobs, retries, resilience, logs, metrics, traces, health, and test types |
| 7 | [Deployment, scaling, and infrastructure](deployment-scaling-infrastructure.md) | Docker, CI/CD, DNS/TLS, proxies, scaling, Kubernetes, storage, and rollouts |

Each chapter has a contents list and previous/next navigation. Start with its **In this project** note or project examples to connect the general concepts to current code. The C# chapter integrates those examples directly into the explanations.

## Study through the application

1. Follow a request in the [architecture guide](../architecture.md), then locate its endpoint and service.
2. Read the [workflow diagrams](../workflows.md) to explain the transaction, duplicate request, payment, and refund paths.
3. Follow an event through [RabbitMQ delivery](../rabbitmq.md) and the [search projection](../product-sync.md).
4. Run the [local demonstration](../demo.md) and [verification checks](../verification.md) to observe the behavior.
5. Use the [design questions](search-and-system-design.md#core-interview-questions-you-should-be-able-to-explain) and [project exercises](search-and-system-design.md#exercises-in-this-repository) to check your understanding.

## General knowledge versus implemented features

The chapters include technologies beyond this application. Redis, Kafka, sagas, read replicas, sharding, Kubernetes, cloud hosting, and CD are learning topics, not installed components. Likewise, metrics, distributed tracing, circuit breakers, rate limiting, and production TLS are not configured here. The [application architecture](../architecture.md) and [current limitations](../../README.md#current-limitations) describe the implementation; the [verification guide](../verification.md) identifies what is actually tested.

For API usage and operational instructions, return to the [documentation index](../README.md). Keep concept explanations in these chapters and link to the implementation guides for commands and exact behavior.
