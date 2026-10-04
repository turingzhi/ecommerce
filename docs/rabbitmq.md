# RabbitMQ event delivery

The SQL Outbox and RabbitMQ cover different stages of event delivery. The [workflow diagrams](workflows.md#outbox-rabbitmq-retries-and-product-search) show both stages; [verification](verification.md) has the commands for checking them locally.

## SQL Outbox to broker

The Outbox worker reads unpublished SQL records and passes them to `RabbitMqEventPublisher`. The publisher declares a durable direct exchange and durable queues, sends a persistent JSON event with the stable Outbox ID, and waits for a publisher confirmation. Mandatory routing makes an unbound routing key fail. Only after confirmation does the dispatcher set `PublishedAt` on the SQL row.

If RabbitMQ is unavailable, the SQL event stays pending and the Outbox worker retries with backoff. Transport failures do not permanently dead-letter it, even after more than five attempts. An unroutable or rejected publication follows the separate five-attempt SQL Outbox dead-letter policy. A confirmation can be received just before the application crashes, leaving the SQL row unpublished; this can cause another publication of the same event.

## Broker to consumer

`RabbitMqConsumerWorker` receives one main-queue delivery at a time and calls `EventConsumer`. On success, the consumer saves the event ID in `ProcessedMessages` before the worker acknowledges RabbitMQ. If an acknowledgement is lost, RabbitMQ can redeliver; the saved ID prevents repeated work. This is at-least-once delivery.

On a processing failure, the worker publishes the original body to a durable retry queue with a confirmed publish, then acknowledges the original delivery. The retry queue has a two-second TTL and routes expired messages back to the main exchange. The consumer reads from the main queue, so it receives the event again when the retry queue sends it back. Non-transient processing failures reach a separate RabbitMQ dead-letter queue on the fifth failed processing attempt. Temporary database and Elasticsearch outages stay retryable without consuming that limit. If moving a delivery to the retry or dead-letter queue fails, the worker requeues the original delivery.

For `ProductUpserted`, the consumer indexes Elasticsearch before saving the processed ID. Other event types currently receive a processed marker but have no fulfillment or email handler. The [product synchronization guide](product-sync.md) explains versioned indexing.

The RabbitMQ verification runner checks confirmed publication, duplicate delivery, unroutable routing, broker outage and recovery, temporary consumer database failure, and a poison event reaching the RabbitMQ dead-letter queue. It uses a temporary SQLite database with the real local broker; see [verification setup](verification.md#sql-server-and-infrastructure-checks).
