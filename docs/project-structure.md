# Project structure

The application has one ASP.NET Core API and one React storefront.
Business code is grouped by feature.

```text
Ecommerce/
├── Ecommerce.sln
├── Dockerfile
├── compose.yaml
├── compose.development.yaml
├── compose.observability.yaml
├── src/
│   ├── Ecommerce.Api/
│   │   ├── Program.cs
│   │   ├── Features/
│   │   │   ├── Accounts/
│   │   │   ├── Catalog/
│   │   │   ├── Cart/
│   │   │   ├── Orders/
│   │   │   ├── Payments/
│   │   │   └── Refunds/
│   │   ├── Infrastructure/
│   │   │   ├── Persistence/Migrations/
│   │   │   ├── Messaging/
│   │   │   ├── Search/
│   │   │   └── Health/
│   │   ├── Common/
│   │   ├── Observability/
│   │   └── Verification/
│   └── storefront/
│       ├── src/features/
│       │   ├── accounts/
│       │   ├── catalog/
│       │   ├── catalog-admin/
│       │   ├── cart/
│       │   ├── orders/
│       │   └── operations-admin/
│       ├── e2e/
│       └── scripts/
├── tests/
│   ├── Ecommerce.Api.Tests/
│   └── http/
├── requests/
├── tools/demo/
└── docs/
```

## Where a feature lives

| Folder | Responsibility |
| --- | --- |
| `Controllers` | Bind requests, check identity/permissions, and return HTTP responses |
| `Services` | Business rules, queries, and transactions |
| `Models` | Persisted business records |
| `Contracts` | HTTP request and response shapes |
| `Infrastructure` | EF context, migrations, messaging, search, and health probes |
| `Common` | Shared security, pagination, and rate limiting |
| `Observability` | Metrics and tracing |

Features only have the subfolders they need. Accounts uses Identity's models;
Cart stores product IDs and quantities in Redis. Services can use EF directly.
These folders are in one API project rather than separate assemblies.
Each controller contains named actions with `[HttpGet]`, `[HttpPost]`, or other HTTP attributes.
Customer and admin controllers are separate. Payment and refund simulator controllers
are discovered only in Development; Identity supplies its built-in account routes.

For one example, follow [ProductAdminController](../src/Ecommerce.Api/Features/Catalog/Controllers/ProductAdminController.cs)
to [ProductCatalogService](../src/Ecommerce.Api/Features/Catalog/Services/ProductCatalogService.cs)
and [ShopDbContext](../src/Ecommerce.Api/Infrastructure/Persistence/ShopDbContext.cs).
The React catalog starts at [CatalogPage](../src/storefront/src/features/catalog/CatalogPage.tsx).
[Program.cs](../src/Ecommerce.Api/Program.cs) registers services and maps controllers with `app.MapControllers()`.

## Naming conventions

C# files use PascalCase. Namespaces follow folders below `Ecommerce`, such as
`Ecommerce.Features.Orders.Services`. Use business names like `OrderService`,
`CreateOrderRequest`, and `OrderResponse`. Related response records may share a file.

React components use PascalCase; helpers use camelCase; feature folders use
lowercase or kebab-case. Unit tests stay beside frontend components or under
`tests/Ecommerce.Api.Tests`. HTTP helpers are in `tests/http/support.py` and
`fixtures.py`; manual requests are in `requests`.

## Commands from the repository root

See [backend setup](../src/Ecommerce.Api/README.md),
[frontend setup](../src/storefront/README.md), [Docker](docker.md), and
[verification](verification.md) for build/run/test commands.
The published API assembly is `Ecommerce.Api.dll`.

For a new migration, install compatible EF tooling and set `ECOMMERCE_SQLSERVER`
for [ShopDbContextFactory](../src/Ecommerce.Api/Infrastructure/Persistence/ShopDbContextFactory.cs):

```sh
dotnet ef migrations add YOUR_MIGRATION --project src/Ecommerce.Api --output-dir Infrastructure/Persistence/Migrations
dotnet ef database update --project src/Ecommerce.Api
```

Keep existing migration timestamp IDs and operations. Historical generated models
can retain old entity names; the current snapshot describes the current model.
See [architecture](architecture.md) to understand the runtime flow.
