# Documentation

Use the [project README](../README.md) for capabilities and quick start. This index organizes the detailed application guides and backend reference.

## Run, use, and verify

| Task | Guide |
| --- | --- |
| Start the local stack | [Docker Compose quick start](../README.md#run-with-docker-compose) |
| Configure services, inspect logs, and diagnose failures | [Local operations](operations.md) |
| Try checkout and search outage recovery | [Local demonstration](demo.md) |
| Send requests and interpret responses | [HTTP API reference](api.md) |
| Run checks and understand CI results | [Verification](verification.md) |

## Understand the implementation

Read architecture first, then follow the workflow or subsystem relevant to your question.

| Guide | Responsibility |
| --- | --- |
| [Application architecture](architecture.md) | Runtime boundaries, request flow, and code map |
| [Workflow diagrams](workflows.md) | Order/payment/refund states, SQL locking, and failure paths |
| [RabbitMQ delivery](rabbitmq.md) | Outbox publication, acknowledgements, retries, and dead letters |
| [Product synchronization](product-sync.md) | Catalog writes, versioned Elasticsearch documents, and recovery |
| [SQL Server order-list measurement](sqlserver-order-list-performance.md) | One measured index change, method, and limitations |

## Backend knowledge

The [backend learning guide](knowledge/README.md) contains seven chapters covering C#, encapsulation, DI, APIs, databases, messaging, search, reliability, and deployment. General examples are distinguished from implemented project features.

## Where documentation belongs

- Root `README.md`: application overview, quick start, and current limitations.
- `docs/`: actual API behavior, workflows, operational commands, verification, and measured results.
- `docs/knowledge/`: reusable concepts and explanations, with links to project examples.

Keep one main explanation per topic. Link to that explanation from other guides; keep diagrams next to the workflow they describe. Use descriptive lowercase, hyphenated filenames, and update links and this index when moving a page. Historical measurements retain their date and scope.
