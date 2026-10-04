# Caching, messaging, and consistency

[Learning index](README.md) · [Documentation index](../README.md)

Understand faster reads and asynchronous work, then distinguish delivery guarantees, duplicate protection, retries, and consistency across systems. Code snippets are general examples unless they link to a repository file.

> **In this project:** There is no Redis cache or Kafka in this app. It uses a SQL Outbox, RabbitMQ, consumer deduplication, and an eventually consistent Elasticsearch product index. The SQL Outbox dead-letter flag and RabbitMQ dead-letter queue are separate. See [RabbitMQ delivery](../rabbitmq.md), [product synchronization](../product-sync.md), and [OutboxDispatcher](../../Services/OutboxDispatcher.cs).

## On this page

- [Cache and Redis](#cache-and-redis)
- [Distributed Cache and Consistent Hashing](#distributed-cache-and-consistent-hashing)
- [Message Queues](#message-queues)
- [Delivery Semantics](#delivery-semantics)
- [Idempotency](#idempotency)
- [Outbox Pattern](#outbox-pattern)
- [MessageId, EntityId, and CorrelationId](#messageid-entityid-and-correlationid)
- [Eventual Consistency](#eventual-consistency)
- [Saga Pattern](#saga-pattern)
- [CAP Theorem](#cap-theorem)
- [Project examples](#project-examples)

## Cache and Redis

### Cache-Aside Pattern

```text
Read Cache
↓
Hit → Return

Miss
↓
Read Database
↓
Write Cache
↓
Return
```

### TTL

**TTL — Time To Live**

Defines how long a cache entry remains valid.

### Cache Invalidation

When database data changes, cache data may become stale.

Common strategies:

- Delete cache entry
- Update cache entry
- Use TTL
- Event-driven invalidation

### Cache Stampede / Thundering Herd

A popular key expires and many requests hit the database at the same time.

Mitigation:

- Locking
- Request coalescing
- TTL jitter
- Background refresh

### Cache Penetration

Repeated requests for data that does not exist.

Mitigation:

- Negative caching
- Bloom Filter
- Rate limiting

### Hot Key

One key receives a disproportionately large amount of traffic.

### Eviction

Common policies:

- LRU
- LFU
- TTL-based eviction

## Distributed Cache and Consistent Hashing

With multiple API instances:

```text
Server A Cache
≠
Server B Cache
```

A distributed cache solves this:

```text
Server A ─┐
Server B ─┼→ Redis
Server C ─┘
```

### Sharding

Distribute keys across multiple cache nodes.

### hash(key) % N

Simple but problematic when the number of nodes changes.

Adding or removing a node can remap many keys.

### Consistent Hashing

Uses a hash ring so node changes only remap part of the key space.

### Virtual Nodes

Each physical node gets multiple positions on the hash ring.

This improves load distribution.

### Replication

Copies the same data to multiple nodes.

Used for:

- Availability
- Failover
- Read scaling

## Message Queues

Common technologies:

- RabbitMQ
- Kafka

Basic model:

```text
Producer
↓
Broker
↓
Consumer
```

### RabbitMQ

Often used for:

- Task queues
- Reliable message delivery
- Work distribution

### Kafka

Often used for:

- Event streaming
- Append-only event logs
- High-throughput pipelines
- Event replay

### Retry

Transient failures may be retried.

Good practices:

- Exponential Backoff
- Jitter
- Retry limit

### Dead Letter Queue

Messages that cannot be processed may be sent to a:

**DLQ — Dead Letter Queue**

Useful for:

- Debugging
- Manual replay
- Monitoring

## Delivery Semantics

| Model | Meaning at a stated delivery boundary |
| --- | --- |
| At-most-once | A message may be lost, but delivery is not retried into duplicates |
| At-least-once | Redelivery is possible; consumers must tolerate duplicates |
| Exactly-once | A guarantee within a specific system or transaction boundary, not automatically across every external effect |

A RabbitMQ publisher confirmation means the broker accepted responsibility; it does not mean the consumer finished or Elasticsearch was updated. Consumer acknowledgement is a separate signal. Lost confirmations or acknowledgements can cause redelivery. See [RabbitMQ acknowledgements and confirms](https://www.rabbitmq.com/docs/confirms).

In practice, a common approach is:

```text
At-least-once
+
Idempotent Consumer
```

## Idempotency

An operation is idempotent if performing it multiple times has the same final effect as performing it once.

Example:

```text
MessageId
↓
ProcessedMessages table
```

If already processed:

```text
skip
```

A database constraint should ideally enforce:

```text
UNIQUE(MessageId)
```

### Retry versus duplicate

A **retry** is another attempt after failure or an uncertain result. A **duplicate** is the same logical operation or event arriving again. Retrying can produce a duplicate when the previous attempt succeeded but its response was lost.

Reuse the original idempotency key for the same request and preserve the message ID for the same event. A new key means a new operation; it defeats replay protection. Reject reuse of a key with different request details.

### The processed marker is not the whole guarantee

Checking `ProcessedMessages` and later writing it leaves a crash window around any intervening effect. For effects in the same SQL database, commit the business change and processed marker together. For an external effect, use that system's idempotency or version mechanism as well.

Here Elasticsearch is updated before the SQL marker. Product ID and version make replay safe after a crash in between. A future email or payment-provider handler would need its own duplicate protection; the marker alone would not make those effects exactly-once.

## Outbox Pattern

Problem:

```text
Save Database
+
Publish Message
```

These are not one atomic operation.

Failure scenario:

```text
DB commit succeeded
Message publish failed
```

Outbox solution:

```text
Database Transaction:
Order
+
OutboxMessage
↓
Commit
```

Background Worker:

```text
Read pending Outbox messages
↓
Publish to Broker
↓
Mark Outbox row as published
```

This ensures business data and pending events are saved atomically.

## MessageId, EntityId, and CorrelationId

- **OrderId** — business entity identifier
- **MessageId** — unique identifier of a message, useful for idempotency
- **CorrelationId** — identifier used to connect events and requests belonging to the same workflow

### Event contracts and schema evolution

An event is a contract between the producer and consumer. Keep the message ID stable across retries, document field meanings, and plan compatibility when adding or changing payload fields. A consumer deployed before a producer must still understand the messages it receives. Adding optional data is usually easier to roll out than renaming a required field.

This project's [BrokerEvent](../../Services/RabbitMqEventPublisher.cs) envelope carries `Id`, `OrderId`, `Type`, and `Payload`; it has no explicit schema-version field. `Product.Version` orders catalog snapshots—it is a business-data version, not an event-schema version. The current product consumer validates snapshot identity and version, but it does not provide a general schema-migration framework.

## Eventual Consistency

Independent distributed systems cannot always share one local transaction.

A common design is:

```text
Local Transaction
+
Reliable Messaging
+
Retry
+
Idempotency
+
Eventual Consistency
```

## Saga Pattern

A Saga coordinates a business workflow across multiple services.

Example:

```text
Create Order
↓
Charge Payment
↓
Reserve Inventory
```

If a later step fails, the system may perform:

**Compensating Transactions**

Two styles:

- **Choreography** — services react to events
- **Orchestration** — a central Saga Orchestrator coordinates steps

## CAP Theorem

- C = Consistency
- A = Availability
- P = Partition Tolerance

The practical interpretation:

> When a network partition occurs, a distributed system must trade off Consistency and Availability.

### Strong Consistency

After a successful write, later reads observe the latest value.

### Eventual Consistency

Temporary inconsistency is allowed, but replicas eventually converge.

### Read-after-Write / Read-your-writes

A client should see its own recent writes.

### Monotonic Reads

After observing a newer version, the client should not later observe an older version.

## Project examples

The project has three related but different duplicate protections:

| Boundary | Stable identity | What a retry does |
| --- | --- | --- |
| Client → order/payment/refund service | Customer plus an idempotency key, or payment plus a refund key | Returns the saved result for the same request; conflicting details are rejected. |
| SQL Outbox → RabbitMQ | [OutboxMessage.Id](../../Models/OutboxMessage.cs) | A publication may be repeated if the broker confirmed it but saving `PublishedAt` failed. |
| RabbitMQ → consumer | Same event ID in [ProcessedMessages](../../Data/ShopDb.cs) | A redelivery can be acknowledged without repeating the consumer effect. |

An **order ID** identifies the business object; an **Outbox message ID** identifies one event about it. A separate end-to-end correlation ID is not implemented. One order may have multiple different events, but a replay of the original checkout does not add another `OrderCreated` row.

The Outbox solves a two-system problem: SQL Server cannot atomically commit an order and publish to RabbitMQ in one local transaction. [OutboxDispatcher](../../Services/OutboxDispatcher.cs) later publishes the committed row, waits for broker confirmation, then marks it published. This is **at-least-once delivery**: a failure between confirmation and the marker can produce another delivery, so the consumer must tolerate duplicates.

The SQL Outbox and RabbitMQ consumer have independent retry counters and dead-letter destinations. Keep their exact attempt limits and exception rules in the [delivery guide](../rabbitmq.md#failure-classification-and-retry-timing); do not apply one stage's counter to the other.

SQL Server is the **source of truth** for products. The consumer copies `ProductUpserted` snapshots into Elasticsearch; search can therefore lag behind a committed catalog write. Checkout still reads stock and price from SQL Server. Elasticsearch uses product ID and version so an older event cannot replace a newer search document. This is the project's concrete example of **eventual consistency**. It does not implement a saga or distributed transaction. See [product synchronization](../product-sync.md).

---

Previous: [Data access and concurrency](data-concurrency.md) · [Learning index](README.md) · Next: [Search and system design](search-and-system-design.md)
