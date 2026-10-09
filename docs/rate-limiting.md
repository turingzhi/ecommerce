# Rate limiting

[Documentation index](README.md) · [API reference](api.md)

## Policies

| Routes | Shared partition | Default allowance |
| --- | --- | --- |
| `GET /products`, `GET /products/{productId}`, `GET /products/search` | Connection remote IP, including authenticated callers | 120 per 60 seconds |
| `POST /orders`, `POST /cart/checkout` | Authenticated customer ID | 30 per 60 seconds |
| Payment/refund creation and their Development-only simulators | Authenticated customer ID across that customer's orders/payments | 20 per 60 seconds |

The last policy covers `POST /orders/{orderId}/payments`, `POST /payments/{paymentId}/refunds`, `POST /dev/payments/{paymentId}/simulate`, and `POST /dev/refunds/{refundId}/simulate`.

Each policy uses an independent fixed window with no request queue. Invalid requests, cache hits, and idempotent retries consume permits. Other routes, including registration, login, order/payment/refund reads, cancellation, and health checks, have no policy.

Middleware runs routing → authentication → authorization → rate limiting. Authorization rejects missing/invalid tokens before customer limiting. Customer IDs come from validated Identity claims. The IP policy normalizes IPv4-mapped IPv6 addresses and ignores raw `X-Forwarded-For`.

## Rejection and retry

An exhausted quota returns 429, numeric `Retry-After`, `Cache-Control: no-store`, and:

```json
{ "error": "Too many requests. Please retry later." }
```

`Retry-After` is the limiter's estimate, rounded up to at least one second. Wait before retrying; another caller sharing the quota may use a new permit first. Keep the original idempotency key/details when retrying an uncertain creation request. Rejection happens before the endpoint reserves stock or creates a payment/refund.

## Configuration

[appsettings.json](../src/Ecommerce.Api/appsettings.json) contains:

```json
{
  "RateLimiting": {
    "SearchPermitLimit": 120,
    "OrderPermitLimit": 30,
    "PaymentPermitLimit": 20,
    "WindowSeconds": 60
  }
}
```

Startup requires limits between 1 and 100,000 and a window between 1 and 3,600 seconds. Restart the process after changes. Compose overrides belong under `services.ecommerce.environment`, for example:

```yaml
RateLimiting__SearchPermitLimit: "60"
RateLimiting__WindowSeconds: "30"
```

Recreate with `docker compose up -d ecommerce`; C# changes also require `--build`. `.env` only substitutes values explicitly referenced by Compose.

[CommerceRateLimiting](../src/Ecommerce.Api/Common/RateLimiting/CommerceRateLimiting.cs) defines policies, partitions, validation, and rejection responses. [Program.cs](../src/Ecommerce.Api/Program.cs) orders middleware; endpoint files attach each policy.

## Test

With the API rebuilt and default limits restored:

```sh
python3 -u tests/http/verify_rate_limit_http.py
```

The check exhausts quotas with invalid requests, without reserving stock or creating orders/payments. It creates two accounts, verifies shared-IP throttling and ignored forwarded headers, checks separate customer and order/payment quotas, and waits about one minute for recovery. Run it separately from HTTP checks using the same client IP. See [verification](verification.md).

## Limits of this design

Counters live in each API process and reset on restart. Several replicas enforce separate quotas; Redis does not store them. Fixed windows allow bursts near a boundary, and shared NAT/proxy addresses share the catalog quota. A deployment behind a reverse proxy needs trusted forwarded-header configuration. Registration/login have no policy, so these limits are only part of traffic control.
