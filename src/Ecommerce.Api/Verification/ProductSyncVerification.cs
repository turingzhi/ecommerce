using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Catalog.Services;
using Ecommerce.Infrastructure.Messaging;
using Ecommerce.Infrastructure.Messaging.Models;
using Ecommerce.Infrastructure.Messaging.Outbox;
using Ecommerce.Infrastructure.Messaging.RabbitMq;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Ecommerce.Verification;

public static partial class VerificationRunner
{
    public static async Task RunProductSync()
    {
        await VerifyProductEventNeedsNoOrder();
        await VerifyProductCatalogWrites();
        await VerifyOlderSnapshotCannotReplaceNewerSearchDocument();
        await VerifyProductEventConsumer();
        await VerifySqlToRabbitToSearch();
        Console.WriteLine("Product synchronization verification passed.");
    }

    private static async Task VerifySqlToRabbitToSearch()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ECOMMERCE_SQLSERVER")
            ?? throw new InvalidOperationException("Set ECOMMERCE_SQLSERVER for --verify-product-sync.");
        var builder = new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = $"EcommerceProductSync_{Guid.NewGuid():N}"
        };
        var connectionString = builder.ConnectionString;
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(connectionString).Options;
        var indexName = $"product-pipeline-verify-{Guid.NewGuid():N}";
        var goodClient = new ElasticsearchClient(new ElasticsearchClientSettings(
            new Uri("http://localhost:9200")));
        var badClient = new ElasticsearchClient(new ElasticsearchClientSettings(
            new Uri("http://127.0.0.1:1")));
        var rabbitOptions = Options.Create(new RabbitMqOptions());
        using var badServices = BuildProductConsumerServices(
            connectionString, new ProductSearchService(badClient, indexName));
        using var goodServices = BuildProductConsumerServices(
            connectionString, new ProductSearchService(goodClient, indexName));
        using var badWorker = new RabbitMqConsumerWorker(rabbitOptions,
            badServices.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RabbitMqConsumerWorker>.Instance);
        using var goodWorker = new RabbitMqConsumerWorker(rabbitOptions,
            goodServices.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RabbitMqConsumerWorker>.Instance);
        var badStarted = false;
        var goodStarted = false;
        Guid? createdMessageId = null;
        try
        {
            await using (var db = new ShopDbContext(options))
                await db.Database.MigrateAsync();

            int productId;
            OutboxMessage created;
            await using (var db = new ShopDbContext(options))
            {
                var product = await new ProductCatalogService(db).CreateAsync(
                    new ProductChange("Pipeline Camera", "First", "Photo", 12000, 2));
                productId = product.Id;
                created = await db.Outbox.SingleAsync();
                createdMessageId = created.Id;
            }

            var retriesBefore = await QueueMessageCountAsync(RabbitMqTopology.RetryQueue);
            var deadBefore = await QueueMessageCountAsync(RabbitMqTopology.DeadQueue);
            await badWorker.StartAsync(CancellationToken.None);
            badStarted = true;
            await using (var db = new ShopDbContext(options))
                await new OutboxDispatcher(db, new RabbitMqEventPublisher(rabbitOptions))
                    .DispatchBatchAsync(CancellationToken.None);
            await WaitUntilAsync(async () =>
                await QueueMessageCountAsync(RabbitMqTopology.RetryQueue) > retriesBefore,
                "Elasticsearch outage did not send product event to RabbitMQ retry queue");
            await using (var db = new ShopDbContext(options))
                Check((await db.Outbox.SingleAsync()).PublishedAt is not null &&
                      await db.ProcessedMessages.CountAsync() == 0,
                    "Broker confirms product event while failed search write remains unacknowledged");

            await Task.Delay(TimeSpan.FromSeconds(11));
            await using (var db = new ShopDbContext(options))
                Check(await db.ProcessedMessages.CountAsync() == 0 &&
                      await QueueMessageCountAsync(RabbitMqTopology.DeadQueue) == deadBefore,
                    "Long Elasticsearch outage keeps product event retryable instead of dead-lettering it");

            await badWorker.StopAsync(CancellationToken.None);
            badStarted = false;
            await goodWorker.StartAsync(CancellationToken.None);
            goodStarted = true;
            await WaitUntilAsync(async () =>
            {
                await using var db = new ShopDbContext(options);
                return await db.ProcessedMessages.AnyAsync(p => p.MessageId == created.Id);
            }, "Product event did not recover after search became available");
            var indexed = await goodClient.GetAsync<ProductSearchDocument>(indexName, productId);
            Check(indexed.Source?.Name == "Pipeline Camera",
                "SQL Outbox event reaches Elasticsearch through RabbitMQ after recovery");

            OutboxMessage updated;
            await using (var db = new ShopDbContext(options))
            {
                await new ProductCatalogService(db).UpdateAsync(productId,
                    new ProductDetailsChange("Pipeline Camera II", "Second", "Photo", 13000));
                updated = (await db.Outbox.ToListAsync())
                    .Single(m => m.Payload.Contains("Pipeline Camera II"));
            }
            await using (var db = new ShopDbContext(options))
                await new OutboxDispatcher(db, new RabbitMqEventPublisher(rabbitOptions))
                    .DispatchBatchAsync(CancellationToken.None);
            await WaitUntilAsync(async () =>
            {
                await using var db = new ShopDbContext(options);
                return await db.ProcessedMessages.AnyAsync(p => p.MessageId == updated.Id);
            }, "Updated product event did not process");
            indexed = await goodClient.GetAsync<ProductSearchDocument>(indexName, productId);
            Check(indexed.Source?.Name == "Pipeline Camera II" &&
                  indexed.Source.PriceCents == 13000,
                "SQL product update automatically refreshes Elasticsearch");

            await new RabbitMqEventPublisher(rabbitOptions)
                .PublishAsync(created, CancellationToken.None);
            await Task.Delay(500);
            indexed = await goodClient.GetAsync<ProductSearchDocument>(indexName, productId);
            await using (var db = new ShopDbContext(options))
                Check(indexed.Source?.Name == "Pipeline Camera II" &&
                      await db.ProcessedMessages.CountAsync() == 2,
                    "Repeated old RabbitMQ delivery cannot undo a newer product update");

            await VerifySqlProductOutboxRollback(options, productId);
        }
        finally
        {
            if (badStarted) await badWorker.StopAsync(CancellationToken.None);
            if (goodStarted) await goodWorker.StopAsync(CancellationToken.None);
            if (createdMessageId.HasValue)
                await RemoveDeadLetterAsync(createdMessageId.Value);
            await goodClient.Indices.DeleteAsync(indexName);
            await using var db = new ShopDbContext(options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static async Task RemoveDeadLetterAsync(Guid messageId)
    {
        var factory = RabbitMqTopology.CreateFactory(new RabbitMqOptions());
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var count = (await channel.QueueDeclarePassiveAsync(
            RabbitMqTopology.DeadQueue)).MessageCount;
        for (var i = 0; i < count; i++)
        {
            var item = await channel.BasicGetAsync(
                RabbitMqTopology.DeadQueue, autoAck: false);
            if (item is null) break;
            if (item.BasicProperties.MessageId == messageId.ToString("D"))
            {
                await channel.BasicAckAsync(item.DeliveryTag, false);
                break;
            }
            // Other deliveries return to the queue when this channel closes.
        }
    }

    private static async Task VerifySqlProductOutboxRollback(
        DbContextOptions<ShopDbContext> options, int existingProductId)
    {
        await using (var db = new ShopDbContext(options))
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE [dbo].[Outbox]
                ADD CONSTRAINT [CK_VerifyProductSyncOutbox]
                CHECK ([Payload] NOT LIKE '%Atomic Failure%')
                """);

        try
        {
            var createFailed = false;
            try
            {
                await using var db = new ShopDbContext(options);
                await new ProductCatalogService(db).CreateAsync(
                    new ProductChange("Atomic Failure Create", "", "Photo", 1000, 1));
            }
            catch (DbUpdateException) { createFailed = true; }
            await using (var db = new ShopDbContext(options))
                Check(createFailed &&
                      !await db.Products.AnyAsync(p => p.Name == "Atomic Failure Create") &&
                      !await db.Outbox.AnyAsync(m => m.Payload.Contains("Atomic Failure Create")),
                    "SQL Server rolls back product creation when its Outbox insert fails");

            var updateFailed = false;
            try
            {
                await using var db = new ShopDbContext(options);
                await new ProductCatalogService(db).UpdateAsync(existingProductId,
                    new ProductDetailsChange("Atomic Failure Update", "", "Photo", 15000));
            }
            catch (DbUpdateException) { updateFailed = true; }
            await using (var db = new ShopDbContext(options))
            {
                var product = await db.Products.SingleAsync(p => p.Id == existingProductId);
                Check(updateFailed && product.Name == "Pipeline Camera II" &&
                      product.PriceCents == 13000 &&
                      !await db.Outbox.AnyAsync(m => m.Payload.Contains("Atomic Failure Update")),
                    "SQL Server rolls back product update when its Outbox insert fails");
            }
        }
        finally
        {
            await using var db = new ShopDbContext(options);
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE [dbo].[Outbox]
                DROP CONSTRAINT [CK_VerifyProductSyncOutbox]
                """);
        }
    }

    private static ServiceProvider BuildProductConsumerServices(
        string connectionString, ProductSearchService search) =>
        new ServiceCollection()
            .AddDbContext<ShopDbContext>(o => o.UseSqlServer(connectionString))
            .AddSingleton(search)
            .AddScoped<EventConsumer>()
            .BuildServiceProvider();

    private static async Task VerifyProductEventConsumer()
    {
        var path = $"Verify_product_consumer_{Guid.NewGuid():N}";
        var connection = VerificationConnection(path);
        var options = new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(connection).Options;
        var client = new ElasticsearchClient(new ElasticsearchClientSettings(
            new Uri("http://localhost:9200")));
        var indexName = $"product-consumer-verify-{Guid.NewGuid():N}";
        var search = new ProductSearchService(client, indexName);
        using var services = new ServiceCollection()
            .AddDbContext<ShopDbContext>(o => o.UseSqlServer(connection))
            .AddSingleton(search)
            .AddScoped<EventConsumer>()
            .BuildServiceProvider();
        try
        {
            OutboxMessage created;
            int productId;
            await using (var db = new ShopDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var product = await new ProductCatalogService(db).CreateAsync(
                    new ProductChange("Lens", "Wide angle", "Photo", 3000, 5));
                productId = product.Id;
                created = await db.Outbox.SingleAsync();
            }
            using (var scope = services.CreateScope())
            {
                var consumer = scope.ServiceProvider.GetRequiredService<EventConsumer>();
                Check(await consumer.ConsumeAsync(created, CancellationToken.None),
                    "First product event is processed");
            }
            var indexed = await client.GetAsync<ProductSearchDocument>(indexName, productId);
            await using (var db = new ShopDbContext(options))
                Check(indexed.Source?.Name == "Lens" &&
                      await db.ProcessedMessages.CountAsync() == 1,
                    "Product event indexes Elasticsearch before recording its processed ID");

            using (var scope = services.CreateScope())
            {
                var consumer = scope.ServiceProvider.GetRequiredService<EventConsumer>();
                Check(!await consumer.ConsumeAsync(created, CancellationToken.None),
                    "Duplicate product event is ignored");
            }

            OutboxMessage updated;
            await using (var db = new ShopDbContext(options))
            {
                await new ProductCatalogService(db).UpdateAsync(productId,
                    new ProductDetailsChange("Lens II", "Wide angle", "Photo", 3500));
                updated = (await db.Outbox.ToListAsync())
                    .Single(m => m.Payload.Contains("Lens II"));
            }
            var unavailableClient = new ElasticsearchClient(new ElasticsearchClientSettings(
                new Uri("http://127.0.0.1:1")));
            using var unavailableServices = new ServiceCollection()
                .AddDbContext<ShopDbContext>(o => o.UseSqlServer(connection))
                .AddSingleton(new ProductSearchService(unavailableClient, indexName))
                .AddScoped<EventConsumer>()
                .BuildServiceProvider();
            var failed = false;
            try
            {
                using var scope = unavailableServices.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EventConsumer>()
                    .ConsumeAsync(updated, CancellationToken.None);
            }
            catch (Exception) { failed = true; }
            await using (var db = new ShopDbContext(options))
                Check(failed && await db.ProcessedMessages.CountAsync() == 1,
                    "Elasticsearch outage leaves product event unprocessed for retry");

            using (var scope = services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<EventConsumer>()
                    .ConsumeAsync(updated, CancellationToken.None);
            indexed = await client.GetAsync<ProductSearchDocument>(indexName, productId);
            await using (var db = new ShopDbContext(options))
                Check(indexed.Source?.Name == "Lens II" &&
                      await db.ProcessedMessages.CountAsync() == 2,
                    "Recovered consumer indexes update and records its event ID");
        }
        finally
        {
            await client.Indices.DeleteAsync(indexName);
            await DeleteVerificationDatabase(options);
        }
    }

    private static async Task VerifyOlderSnapshotCannotReplaceNewerSearchDocument()
    {
        var client = new ElasticsearchClient(new ElasticsearchClientSettings(
            new Uri("http://localhost:9200")));
        var indexName = $"product-sync-verify-{Guid.NewGuid():N}";
        var search = new ProductSearchService(client, indexName);
        await search.EnsureIndexAsync();
        var id = Random.Shared.Next(500_000_000, 1_500_000_000);
        var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            await search.IndexProductAsync(new Product
            {
                Id = id, Name = "Newest Camera", Category = "Electronics",
                PriceCents = 14000, Available = 4, Version = version
            });
            await search.IndexProductAsync(new Product
            {
                Id = id, Name = "Old Camera", Category = "Electronics",
                PriceCents = 12000, Available = 3, Version = version - 1
            });
            var saved = await client.GetAsync<ProductSearchDocument>(indexName, id);
            Check(saved.Source?.Name == "Newest Camera" &&
                  saved.Source.PriceCents == 14000,
                "An older product event cannot overwrite a newer search document");
        }
        finally
        {
            await client.Indices.DeleteAsync(indexName);
        }
    }

    private static async Task VerifyProductCatalogWrites()
    {
        var path = $"Verify_product_catalog_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using (var db = new ShopDbContext(options))
                await db.Database.EnsureCreatedAsync();

            int productId;
            long firstVersion;
            await using (var db = new ShopDbContext(options))
            {
                var product = await new ProductCatalogService(db).CreateAsync(
                    new ProductChange("Camera", "First model", "Electronics", 12000, 3));
                productId = product.Id;
                firstVersion = product.Version;
            }
            await using (var db = new ShopDbContext(options))
            {
                var message = await db.Outbox.SingleAsync();
                using var payload = JsonDocument.Parse(message.Payload);
                Check(message.OrderId is null && message.Type == "ProductUpserted" &&
                      payload.RootElement.GetProperty("Id").GetInt32() == productId &&
                      payload.RootElement.GetProperty("Name").GetString() == "Camera" &&
                      payload.RootElement.GetProperty("PriceCents").GetInt64() == 12000 &&
                      payload.RootElement.GetProperty("Version").GetInt64() == firstVersion,
                    "Create saves a product snapshot and order-free Outbox event");
            }

            await using (var db = new ShopDbContext(options))
                await new ProductCatalogService(db).UpdateAsync(productId,
                    new ProductDetailsChange("Camera II", "New model", "Electronics", 14000));
            await using (var db = new ShopDbContext(options))
            {
                var product = await db.Products.SingleAsync(p => p.Id == productId);
                var events = await db.Outbox.ToListAsync();
                var updated = events.Single(m => m.Payload.Contains("Camera II"));
                using var payload = JsonDocument.Parse(updated.Payload);
                Check(product.Name == "Camera II" && product.PriceCents == 14000 &&
                      product.Version > firstVersion && events.Count == 2 &&
                      payload.RootElement.GetProperty("Version").GetInt64() == product.Version,
                    "Update advances product version and saves its matching Outbox snapshot");
            }

            await using (var db = new ShopDbContext(options))
                await db.Products.Where(p => p.Id == productId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(p => p.Available, 2));
            await using (var db = new ShopDbContext(options))
                await new ProductCatalogService(db).UpdateAsync(productId,
                    new ProductDetailsChange("Camera III", "Newest model", "Electronics", 16000));
            await using (var db = new ShopDbContext(options))
                Check((await db.Products.SingleAsync()).Available == 2,
                    "Catalog details update preserves stock changed by checkout");

            await using (var db = new ShopDbContext(options))
                await db.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE [dbo].[Outbox] WITH NOCHECK
                    ADD CONSTRAINT [CK_VerifyFailProductOutbox] CHECK (1 = 0)
                    """);
            var failed = false;
            try
            {
                await using var db = new ShopDbContext(options);
                await new ProductCatalogService(db).UpdateAsync(productId,
                    new ProductDetailsChange("Should Roll Back", "", "Electronics", 15000));
            }
            catch (DbUpdateException) { failed = true; }
            await using (var db = new ShopDbContext(options))
            {
                Check(failed && (await db.Products.SingleAsync()).Name == "Camera III" &&
                      await db.Outbox.CountAsync() == 3,
                    "Outbox insert failure rolls back the product change");
            }
        }
        finally { await DeleteVerificationDatabase(options); }
    }

    private static async Task VerifyProductEventNeedsNoOrder()
    {
        var path = $"Verify_product_outbox_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer(VerificationConnection(path)).Options;
        try
        {
            await using var db = new ShopDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Products.Add(new Product
            {
                Name = "Camera",
                Category = "Electronics",
                PriceCents = 12000,
                Available = 3
            });
            db.Outbox.Add(new OutboxMessage
            {
                Type = "ProductUpserted",
                Payload = "{}"
            });
            await db.SaveChangesAsync();
            Check(await db.Outbox.CountAsync(m => m.Type == "ProductUpserted") == 1,
                "A product event can be saved without an order reference");
        }
        finally { await DeleteVerificationDatabase(options); }
    }
}
