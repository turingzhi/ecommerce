# RabbitMQ event delivery

[Documentation index](README.md) · [Project overview](../README.md)

The SQL Outbox keeps events until publication succeeds. RabbitMQ delivers them to the consumer. Together they provide at-least-once delivery: an event can arrive more than once, so consumers must tolerate replay.

## SQL Outbox to broker

The Outbox worker polls every five seconds and reads up to 100 eligible unpublished rows, ordered by ID. `RabbitMqEventPublisher` declares the topology, publishes persistent JSON with the stable Outbox ID, and waits for confirmation. Mandatory routing rejects an unbound routing key. Only then does the dispatcher set SQL `PublishedAt`.

A crash after confirmation but before saving `PublishedAt` can publish the event again. Publication failures increment `AttemptCount`, save the error, end that batch, and set `NextAttemptAt` using `min(300, 2^AttemptCount)` seconds of backoff.

| Failure | SQL Outbox policy |
| --- | --- |
| `BrokerDeliveryUnavailableException` | Keep retrying, including beyond five attempts |
| Other publication failure | Dead-letter the SQL row once total `AttemptCount` reaches five |

Unavailable attempts remain in the total, so a later non-transport failure can dead-letter immediately. The publisher preserves `PublishException` and requested cancellation; it wraps other failures as delivery unavailable. This classification is broad: repeated retries can still need operator investigation.

Sources: [OutboxDispatcher](../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxDispatcher.cs), [OutboxWorker](../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxWorker.cs), and [RabbitMqEventPublisher](../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqEventPublisher.cs).

## Broker to consumer

The worker receives one main-queue delivery at a time with manual acknowledgements. `EventConsumer` skips saved event IDs. It handles `ProductUpserted` through Elasticsearch and Redis, and `OrderPaid` through shipment creation; other events only receive a processed marker. Successful processing saves the SQL marker before acknowledgement.

On failure, the worker confirms a replacement publication to the retry or dead queue before acknowledging the original. If that move fails, it requeues the original. The retry queue expires messages after 2,000 ms and routes them back to the main queue.

| Processing failure | Consumer policy |
| --- | --- |
| `ProductSearchUnavailableException`, or a `DbException`/`DbUpdateException` anywhere in the exception chain | Retry without increasing `x-attempts` |
| Other failure | Increase `x-attempts`; the fifth counted failure goes to the dead queue |

Redis generation failures use the counted policy. The database classification also includes errors that may need a data or code fix; unlimited retry does not prove an outage is temporary. The worker reconnects five seconds after a disconnected session.

For products, indexing and cache invalidation precede the marker; versioned writes tolerate repetition. For paid orders, shipment, initial history, and marker commit together under an order-row lock. See [product synchronization](product-sync.md), [fulfillment](fulfillment.md), and [consumer code](../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqConsumerWorker.cs).

## Local topology

All exchanges are durable direct exchanges; all queues are durable. Every binding uses `commerce.event`.

| Exchange | Queue |
| --- | --- |
| `ecommerce.events` | `ecommerce.events.consumer` |
| `ecommerce.events.retry` | `ecommerce.events.retry.consumer` |
| `ecommerce.events.dead` | `ecommerce.events.dead.consumer` |

Defaults are host `localhost`, port `5672`, and user/password `guest`; Compose sets the host to `rabbitmq`. Configuration uses the `RabbitMq` section. The retry queue has a two-second TTL and a dead-letter binding back to the main exchange. See [RabbitMqTopology](../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqTopology.cs) and [RabbitMqOptions](../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqOptions.cs).

Inspect queue counts locally:

```sh
docker compose exec rabbitmq rabbitmqctl list_queues name messages_ready messages_unacknowledged
docker compose logs --since=5m ecommerce
```

The management UI is at [localhost:15672](http://127.0.0.1:15672). SQL dead-letter rows and RabbitMQ dead-queue messages require separate inspection and manual recovery; see [Docker operations](docker.md).

## Verification

`--verify-rabbitmq` exercises confirmed publication, duplicates, unroutable routing, outages/recovery, poison messages, shipment replay, and broker trace correlation against real local infrastructure. It uses a disposable SQL database. Pause the API consumer before running it so the API does not take the verifier's messages. See [verification](verification.md) for setup and commands.
