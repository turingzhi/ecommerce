# HTTP examples

These `.http` files are manual examples for an HTTP client such as the VS Code
REST Client extension or JetBrains HTTP Client.

1. Start the [Docker stack](../README.md#run-with-docker-compose).
2. Open `auth.http` to register and sign in.
3. Copy the returned token into the bearer-token placeholder in protected requests.
4. Follow the product, cart, order, payment, shipment, refund, or return examples.

The default API address is `http://127.0.0.1:5088`. Replace sample IDs and
idempotency keys with values for your own requests.

Admin requests need the permissions described in [admin access](../docs/admin.md).
Payment and refund simulation requires the [Development overlay](../docs/payments.md).

See the [API reference](../docs/api.md) for contracts and the [workflow guide](../docs/workflows.md)
for state changes. Automated HTTP checks live in `tests/http`.
