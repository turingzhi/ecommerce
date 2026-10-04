# RabbitMQ event delivery

[Documentation index](README.md) · [Project overview](../README.md)

The SQL Outbox and RabbitMQ cover different stages of event delivery. The [workflow diagrams](workflows.md#outbox-rabbitmq-retries-and-product-search) show both stages; [verification](verification.md) has the commands for checking them locally.

## SQL Outbox to broker

The Outbox worker reads unpublished SQL records and passes them to `RabbitMqEventPublisher`. The publisher declares a durable direct exchange and durable queues, sends a persistent JSON event with the stable Outbox ID, and waits for a publisher confirmation. Mandatory routing makes an unbound routing key fail. Only after confirmation does the dispatcher set `PublishedAt` on the SQL row.

If RabbitMQ is unavailable, the SQL event stays pending and the Outbox worker retries with backoff. Transport failures do not permanently dead-letter it, even after more than five attempts. They still increase the Outbox attempt count; once that count is at least five, a later non-transport publication failure can dead-letter the SQL row. A confirmation can be received just before the application crashes, leaving the SQL row unpublished; this can cause another publication of the same event.

## Broker to consumer

`RabbitMqConsumerWorker` receives one main-queue delivery at a time and calls `EventConsumer`. On success, the consumer saves the event ID in `ProcessedMessages` before the worker acknowledges RabbitMQ. If an acknowledgement is lost, RabbitMQ can redeliver; the saved ID prevents repeated work. This is at-least-once delivery.

On a processing failure, the worker publishes the original body to a durable retry queue with a confirmed publish, then acknowledges the original delivery. The retry queue has a two-second TTL and routes expired messages back to the main exchange. The consumer reads from the main queue, so it receives the event again when the retry queue sends it back. Non-transient processing failures reach a separate RabbitMQ dead-letter queue on the fifth failed processing attempt. Temporary database and Elasticsearch outages stay retryable without consuming that limit. If moving a delivery to the retry or dead-letter queue fails, the worker requeues the original delivery.

For `ProductUpserted`, the consumer indexes Elasticsearch before saving the processed ID. Other event types currently receive a processed marker but have no fulfillment or email handler. The [product synchronization guide](product-sync.md) explains versioned indexing.

The RabbitMQ verification runner checks confirmed publication, duplicate delivery, unroutable routing, broker outage and recovery, temporary consumer database failure, and a poison event reaching the RabbitMQ dead-letter queue. It uses a temporary SQL Server database with the real local broker; see [verification setup](verification.md#sql-server-and-infrastructure-checks).

## Failure classification and retry timing

The workers classify exceptions; they do not independently know whether a dependency has recovered. A later operation succeeding is how recovery becomes visible.

| Stage | Policy in the current code |
| --- | --- |
| SQL publication | `OutboxWorker` polls every five seconds. After a failure, the dispatcher increments `AttemptCount` and sets a delay of `min(300, 2^AttemptCount)` seconds. A row is eligible only after `NextAttemptAt`. A failed row ends the current batch. |
| SQL dead-letter flag | `BrokerDeliveryUnavailableException` keeps the row retryable. Any other failure dead-letters it when the accumulated count is at least five. Earlier unavailable attempts remain in that count. |
| RabbitMQ consumer | The retry queue has a 2,000 ms TTL and dead-letters expired messages back to the main exchange. Actual processing may take longer because of queues and scheduling. |
| RabbitMQ dead-letter queue | Counted failures increase `x-attempts`; the fifth goes to the dead queue. `ProductSearchUnavailableException` and database exceptions bypass this increment. |

These classifications are deliberately broad in the current implementation. The publisher wraps exceptions other than `PublishException` and requested cancellation as delivery-unavailable. The consumer treats any `DbException` or `DbUpdateException` in the exception chain as an infrastructure failure, including failures that might need a code or data fix. Consequently, unlimited retry does not prove that a failure is temporary. Inspect repeated errors rather than assuming every retry loop will recover automatically.

The code is in [OutboxDispatcher](../Services/OutboxDispatcher.cs), [RabbitMqEventPublisher](../Services/RabbitMqEventPublisher.cs), and [RabbitMqConsumerWorker](../Services/RabbitMqConsumerWorker.cs). For inspection commands and what remains manual, see [operations](operations.md#inspect-event-delivery).
