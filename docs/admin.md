# Admin access

[Documentation index](README.md) · [API reference](api.md) · [Storefront](storefront.md)

Admin access consists of three independent permissions. Customer routes still check ownership, and admin actions still enforce financial and state rules. Run the commands below from the repository root.

## Default account

To create a local account with all three permissions, set your own credentials in `.env`:

```dotenv
DEFAULT_ADMIN_ENABLED=true
DEFAULT_ADMIN_EMAIL=admin@ecommerce.local
DEFAULT_ADMIN_PASSWORD=YOUR_OWN_INITIAL_PASSWORD
```

Setup is disabled in `.env.example`. Use a password accepted by Identity. `.env` is excluded from Git and the Docker build context. Recreate the API to load changed settings; `restart` does not load them:

```sh
docker compose up -d --build --wait ecommerce
```

If using the observability overlay, keep it in the command:

```sh
docker compose -f compose.yaml -f compose.observability.yaml up -d --build --wait ecommerce
```

Startup applies migrations, finds the configured email, creates a missing account with a hashed password, and adds missing permissions. Sign in at [the storefront](http://127.0.0.1:5088/#/login); no separate registration or grants are needed.

For an existing account, startup preserves its ID, password, profile, security stamp, and other claims. Sequential starts do not duplicate grants. Setup assumes one API process. The configured password is only for initial creation: changing it does not reset an existing password. After creation, you may clear the setup password and recreate the API; retain the original login password.

Invalid email or missing/invalid initial credentials stop setup without printing the password. Disabling setup retains the account and claims; changing its email leaves the old account intact. Sign out and back in after any permission change because existing tokens keep their original claims. Public registration grants no admin permissions.

## Permissions

| Permission | Allowed operations | Storefront page |
| --- | --- | --- |
| `products:manage` | List, create, edit products | Products admin |
| `orders:read` | Read orders across customers | Orders admin |
| `payments:read` | Read payment attempts across customers | Payments admin |

There is no universal admin bypass, user-management page, or unrestricted order/payment editor. Product deletion and stock editing are not provided.

To grant only selected permissions to an already registered account, run the commands it needs:

```sh
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --grant-product-admin operator@example.com
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --grant-order-reader operator@example.com
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --grant-payment-reader operator@example.com
```

Grants are independent and repeat-safe. There is no public grant endpoint. Log in again after granting access; ordinary customers cannot supply permissions in a request.

## Orders and payments

[Orders admin](http://127.0.0.1:5088/#/admin/orders) and [Payments admin](http://127.0.0.1:5088/#/admin/payments) show customer IDs, statuses, dates, filters, pagination, and refresh controls. Details show saved purchased items/prices or payment order/amount/currency. Both permissions enable links between order and payment details. A payment-only reader sees the order ID without an order-detail link. These pages are read only.

| Endpoint | Permission | Response |
| --- | --- | --- |
| `GET /admin/orders` | `orders:read` | `{page,pageSize,total,orders}` |
| `GET /admin/orders/{id}` | `orders:read` | Order details, `customerId`, and `orderItems` |
| `GET /admin/payments` | `payments:read` | `{page,pageSize,total,payments}` |
| `GET /admin/payments/{id}` | `payments:read` | Payment details and `customerId` |

Lists accept `status`, `page`, and `pageSize`; payments also accept an `orderId` GUID. Status trims, blanks mean no filter, and values are case-sensitive: orders `PendingPayment`, `Paid`, `Cancelled`; payments `Pending`, `Succeeded`, `Failed`, `Unknown`. Invalid statuses, query GUIDs, and numeric values return 400.

Defaults are page 1 and size 20. Nonpositive values become 1, size is capped at 50, and results sort by creation date then ID descending. Extreme offsets return an empty list with its total. Pages and totals may change between requests.

Anonymous requests return 401, missing permissions 403, and missing detail records 404. Authorized responses use no-store and UTC dates; they omit idempotency keys, tokens, and Identity internals. The UI reports unsupported large monetary amounts instead of rounding them. See [editable requests](../requests/admin-reads.http).

## Code and checks

Implementation: [DefaultAdminBootstrapper](../src/Ecommerce.Api/Common/Security/DefaultAdminBootstrapper.cs), [options](../src/Ecommerce.Api/Common/Security/DefaultAdminOptions.cs), [Program](../src/Ecommerce.Api/Program.cs), [Compose](../compose.yaml), [order endpoints](../src/Ecommerce.Api/Features/Orders/Controllers/OrderAdminController.cs), [payment endpoints](../src/Ecommerce.Api/Features/Payments/Controllers/PaymentAdminController.cs), and [admin UI](../src/storefront/src/features/operations-admin).

```sh
dotnet test Ecommerce.sln
python3 tests/http/verify_admin_reads_http.py
python3 tests/http/verify_default_admin_http.py --restart
```

HTTP checks require the running stack and retain fresh test data. The default-account check requires enabled setup; `--restart` checks that the ID, password hash, and permissions survive API restart. If the setup password was cleared, pass the login password privately through `ECOMMERCE_TEST_ADMIN_PASSWORD`. Credentials are not printed. See [verification](verification.md) for frontend and browser checks.
