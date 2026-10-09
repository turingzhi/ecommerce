# Ecommerce API

ASP.NET Core on .NET 10. The API serves the storefront and runs the background
workers for orders and messaging.

## Find the code

- `Program.cs`: configuration, service registration, startup, and route mapping.
- `Features`: accounts, catalog, cart, orders, payments, and refunds.
- `Features/*/Controllers`: HTTP routes for each feature.
- `Features/*/Services`: business rules and database operations.
- `Infrastructure`: SQL persistence and migrations, RabbitMQ, search, and health checks.
- `Common`: shared security, pagination, and rate limiting.
- `Observability`: metrics and tracing.
- `Verification`: integration checks that run through CLI flags.

See [project structure](../../docs/project-structure.md) and [architecture](../../docs/architecture.md).

## Build and run

From the repository root with the .NET 10 SDK:

```sh
dotnet build Ecommerce.sln
dotnet test Ecommerce.sln
```

For startup, use the [Docker quick start](../../README.md#run-with-docker-compose).
For host development, set the SQL connection string and start the dependencies
as described in [Docker operations](../../docs/docker.md), then run:

```sh
dotnet run --project src/Ecommerce.Api -- --urls http://127.0.0.1:5088
```

The published assembly is `Ecommerce.Api.dll`. See the [API reference](../../docs/api.md)
and [verification guide](../../docs/verification.md) for requests and integration checks.
