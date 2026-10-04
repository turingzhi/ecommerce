# Study notes

This page keeps learning goals separate from the [project README](../README.md). For the implemented HTTP behavior and system design, use the [API reference](api.md), [workflow diagrams](workflows.md), and [verification guide](verification.md). The [C# fundamentals guide](csharp-fundamentals.md) explains the language and dependency-injection concepts used in the code.

## Topics covered in this project

1. ASP.NET Core routes, DTOs, authentication, dependency injection, and background services.
2. EF Core migrations, relational modeling, transactions, and SQL Server locking for concurrent requests.
3. Inventory-backed checkout, customer ownership, idempotency keys, and payment and refund state transitions.
4. Transactional Outbox publication, RabbitMQ delivery, retry queues, and consumer deduplication.
5. Elasticsearch product indexing and the difference between authoritative SQL data and an eventually consistent search copy.
6. Verification with SQLite, SQL Server, RabbitMQ, Elasticsearch, and a local Compose stack.

## Further exercises

- Add HTTP integration tests for registration, login, ownership, checkout, payment-attempt creation, and response contracts. The current custom runners exercise services and infrastructure; CI also checks health and product search over HTTP.
- Add catalog search filters, sorting, and pagination, then measure their query behavior.
- Coordinate background workers before running multiple API replicas.
- Add observability and deployment configuration for an environment beyond the local Compose stack.
- Integrate a payment provider only after defining callbacks, reconciliation, and operational failure handling. Current outcomes are local simulations.

## Code map

| Location | Responsibility |
| --- | --- |
| [Program.cs](../Program.cs) | Service registration, startup migrations and seeding, endpoint mapping, and CLI commands |
| [Endpoints/](../Endpoints/OrderEndpoints.cs) | HTTP routes for authentication, orders, payments, products, and health |
| [Dtos/](../Dtos/CreateOrder.cs) | Request and response shapes |
| [Services/](../Services/OrderService.cs) | Checkout, payment, refund, catalog, Outbox, and background workflows |
| [Data/ShopDb.cs](../Data/ShopDb.cs) | EF Core context and model configuration |
| [Search/](../Search/ProductSearchService.cs) | Elasticsearch indexing and search |
| [Verification/](../Verification/VerificationRunner.cs) | Custom scenario runners |
| [.github/workflows/ecommerce.yml](../.github/workflows/ecommerce.yml) | CI build and verification |

For a step-by-step local walkthrough, see [demo.md](demo.md). For the SQL Server index measurement, see [sqlserver-order-list-performance.md](sqlserver-order-list-performance.md).
