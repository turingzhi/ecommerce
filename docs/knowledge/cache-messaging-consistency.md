# Caching, messaging, and consistency

[Learning index](README.md) · [Documentation index](../README.md)

Learn when a cache helps, how messages survive failures, and why retries need duplicate protection. Snippets are teaching examples unless linked to source code.

**In this project:** Redis stores [customer carts](../cart.md) and caches search responses. A SQL Outbox sends events through RabbitMQ. Consumers update Elasticsearch and create shipments. Kafka, sagas, and cache sharding are learning topics, not installed features.

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

A cache keeps a reusable copy of data to make reads cheaper. Decide what may be stale, how long it may be stale, and what happens when the cache is unavailable.

### Cache-Aside Pattern

```text
Read cache → hit: return cached value
           → miss: read backing store → cache result → return result
```

This project's search backing store is Elasticsearch. SQL Server remains authoritative for product price and stock at checkout.

### TTL

Time to live (TTL) sets how long a key remains before expiring. It limits how long cached data survives, but does not guarantee that it is current before expiry.

Search responses expire after 30 seconds here. Nonempty carts refresh a seven-day inactivity expiry when accessed or changed. A cart is user state stored in Redis, not merely a disposable copy of SQL data; see [Redis](../redis.md) for recovery and persistence limits.

### Cache Invalidation

When backing data changes, delete its cached copy, update it, wait for TTL, or invalidate through an event. Each approach has failure windows.

The product consumer increments `products:search:generation` after indexing a snapshot. Search keys include that generation, so later requests use a new set of keys. Old keys expire naturally. This reduces stale-cache reads but does not remove the delay before the event is consumed.

### Cache Stampede / Thundering Herd

When a popular key expires, many misses can hit the backing store at once. Coalesce identical requests, refresh in the background, vary expiries with jitter, or use bounded coordination. This project does not implement stampede protection.

### Cache Penetration

Repeated lookups for nonexistent data still cost work. Short-lived negative caching can help. A Bloom filter can rule out some impossible lookups, but false positives still require a real check. Rate limiting can reduce abuse.

### Hot Key

A hot key receives much more traffic than other keys. Sharding alone may not fix it because that key still maps to one node. Consider local copies, request coalescing, or a different data layout after measuring.

### Eviction

A cache under memory pressure may evict keys. LRU favors recently used data; LFU favors frequently used data. Some policies choose among keys with TTLs. Expiration and eviction are different: one follows a key's clock, the other frees capacity. Do not assume cart data has the same loss tolerance as cached search results.

## Distributed Cache and Consistent Hashing

Separate API instances have separate in-memory caches. A shared Redis service lets them read the same keys:

```text
API A ─┐
API B ─┼→ Redis
API C ─┘
```

### Sharding

Cache sharding divides keys across nodes. It increases capacity, but adds routing and failure-handling work.

### hash(key) % N

Modulo routing is simple: hash a key and take the remainder for `N` nodes. Changing `N` moves many keys, causing misses and load on the backing store.

### Consistent Hashing

Consistent hashing places keys and nodes on a ring. Adding or removing a node moves only part of the key space. This is one general routing approach; the local Redis service here is a single node.

### Virtual Nodes

Give each physical node several positions on the ring to spread keys more evenly. This helps balance key distribution, though one very hot key can still dominate traffic.

### Replication

Replication copies data to other nodes for failover, availability, or read capacity. It differs from sharding, which splits data. Replication lag and failover behavior affect what callers may see or lose.

## Message Queues

A producer publishes work to a broker; a consumer processes it later:

```text
Producer → broker → consumer
```

This allows the producer and consumer to work at different speeds and recover independently. It also introduces delays, retries, and operational backlog.

### RabbitMQ

RabbitMQ supports queues, routing, acknowledgements, and work distribution. Here the Outbox publisher sends events and the worker consumes them. Publisher confirmation and consumer acknowledgement are separate steps.

### Kafka

Kafka stores ordered records in partitioned logs. Consumers track their offsets, making retention and replay central to its model. It is often used for event streams and high-throughput pipelines. It is not installed in this project.

### Retry

Retry transient failures with increasing delay, jitter, and a suitable budget. Retrying immediately can worsen an outage. A retry is safe only when the logical operation tolerates another attempt.

### Dead Letter Queue

A dead-letter queue (DLQ) holds messages that cannot proceed normally. Use it to inspect failures, alert an operator, and replay after fixing the cause. A DLQ is not successful business processing.

The SQL Outbox's `DeadLettered` flag and RabbitMQ's DLQ are separate destinations with separate retry rules. See [RabbitMQ delivery](../rabbitmq.md).

## Delivery Semantics

| Model | Meaning at the stated boundary |
| --- | --- |
| At-most-once | A message may be lost; that delivery path does not retry it |
| At-least-once | A message may arrive again; consumers must tolerate duplicates |
| Exactly-once | A guarantee within a defined system or transaction boundary |

A broker confirmation means RabbitMQ accepted responsibility for a publication. It does not mean the consumer finished or Elasticsearch changed. Lost confirmations or acknowledgements can cause redelivery. See [RabbitMQ acknowledgements and confirms](https://www.rabbitmq.com/docs/confirms).

A common design is at-least-once delivery plus an idempotent consumer. That aims to avoid duplicate business effects even when transport delivers duplicates.

## Idempotency

An operation is idempotent when repeating the same logical operation has the same final effect as performing it once. A processed-message table can identify a repeated event, with a primary key or unique constraint on `MessageId`.

### Retry versus duplicate

A retry is another attempt after a failure or uncertain result. A duplicate is the same operation arriving again. A retry can become a duplicate if the earlier attempt succeeded but its response was lost.

Reuse the original request key and event ID. A new key asks for a new operation. Reject the same key with different details.

### The processed marker is not the whole guarantee

An effect and a later processed marker leave a crash window. For changes in one SQL database, save the effect and marker in the same transaction. For an external effect, also use that system's idempotency or version checks.

Here product indexing happens before the SQL marker. Elasticsearch's product ID and version make a repeated snapshot safe. The `OrderPaid` handler saves the shipment, initial history, and marker in one SQL transaction. A future email or payment-provider handler would need its own duplicate protection.

## Outbox Pattern

Saving SQL data and publishing to RabbitMQ are two operations. A failure can occur after the SQL commit but before publication.

The Outbox saves the pending event alongside the business change:

```text
SQL transaction: save order + Outbox row → commit
Worker: read pending row → publish → confirm → mark published
```

The transaction guarantees that the order and pending event exist together. The worker can retry delivery. If publication succeeds but saving the published marker fails, it can publish again; the consumer must tolerate that replay.

## MessageId, EntityId, and CorrelationId

| Identifier | Purpose |
| --- | --- |
| Entity ID, such as `OrderId` | Identifies the business object |
| `MessageId` | Identifies one event and stays stable on retry |
| Correlation ID | Connects related work across boundaries |

One order can have many events. Retrying one event preserves its message ID; creating another event uses another ID.

The app carries W3C `TraceParent` and `TraceState` from SQL Outbox rows through RabbitMQ so delayed work can join a distributed trace. That trace context differs from the order ID and message ID. A separate business correlation-ID field is not defined.

### Event contracts and schema evolution

An event's fields and meanings form a producer/consumer contract. Keep IDs stable on retry, add optional fields carefully, and plan changes so older consumers can still read new messages.

The [BrokerEvent](../../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqEventPublisher.cs) envelope has `Id`, optional `OrderId`, `Type`, `Payload`, and optional trace fields. It has no explicit schema-version field. `Product.Version` orders product snapshots; it is not a payload-schema version. Validating snapshot identity and version does not provide a general schema-migration system.

## Eventual Consistency

A local transaction can commit before another system receives its event. With successful delivery, retries, and replay-safe processing, the downstream copy can catch up. During that delay, reads may differ.

For example, catalog changes commit in SQL before the consumer updates Elasticsearch. Checkout still uses SQL price and stock. Monitor failed or aging events; eventual consistency needs a working recovery path.

## Saga Pattern

A saga coordinates a workflow across services that cannot share one local transaction. For example, reserve inventory, request payment, then arrange delivery.

If a later step fails, a compensating action may release the reservation or request a refund. Compensation is another business operation, not a database rollback; it can fail and need a retry too.

In choreography, services react to events. In orchestration, one coordinator decides the next step. The project's order, payment, shipment, and return workflows do not form an implemented distributed saga.

## CAP Theorem

CAP concerns distributed data during a network partition:

- **Consistency:** operations behave as if there is one current copy, in the theorem's strong sense.
- **Availability:** every request to a non-failing node receives a response under the theorem's rules.
- **Partition tolerance:** nodes must cope with messages between them being lost or delayed.

During a partition, a system cannot guarantee both that consistency and that availability for every operation. This is not a permanent “choose any two” label for the whole application. Decide which operations may serve older data and which should wait or fail.

### Strong Consistency

A linearizable system makes a successful write visible to later reads as if operations used one ordered copy. Specify the scope of this guarantee; separate caches and search indexes do not inherit it automatically.

### Eventual Consistency

Copies converge if updates stop and replication or event processing continues successfully. It does not promise a fixed maximum delay by itself.

### Read-after-Write / Read-your-writes

A client can read its own successful update. Routing the immediate read to the authoritative store is one way to provide this.

### Monotonic Reads

Once a client sees a newer version, it should not later see an older one. Switching between lagging replicas can violate this unless the system tracks or routes around it.

## Project examples

| Boundary | Replay identity | Protection |
| --- | --- | --- |
| Client → order service | Customer plus idempotency key | Same details return the saved order |
| Client → payment/refund service | Order/payment plus idempotency key | Same attempt replays; conflicting details fail |
| SQL Outbox → RabbitMQ | Outbox message ID | Repeated publication preserves event identity |
| RabbitMQ → consumer | Processed-message ID | Saved markers identify completed events |

[OutboxDispatcher](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxDispatcher.cs) marks publication only after broker confirmation. Its retry counter differs from the consumer's counter; see [failure classification](../rabbitmq.md) for exact limits.

[EventConsumer](../../src/Ecommerce.Api/Infrastructure/Messaging/EventConsumer.cs) applies `ProductUpserted` snapshots to Elasticsearch and changes the cache generation. Product versions stop an older event replacing a newer search document. `OrderPaid` goes to [ShipmentEventHandler](../../src/Ecommerce.Api/Features/Shipments/Services/ShipmentEventHandler.cs), which checks SQL payment/order state and protects shipment creation with locks and uniqueness. See [product synchronization](../product-sync.md) and [fulfillment](../fulfillment.md).

---

Previous: [Data access and concurrency](data-concurrency.md) · [Learning index](README.md) · Next: [Search and system design](search-and-system-design.md)
