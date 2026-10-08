# Tests

Run commands from the repository root.

## Unit tests

Use the .NET 10 SDK and Node 22 (22.12 or newer within that major version):

```sh
dotnet test Ecommerce.sln
npm --prefix src/storefront ci
npm --prefix src/storefront test
```

These tests do not need Docker. Backend tests are in `Ecommerce.Api.Tests`;
frontend tests are beside the components they check. Controller HTTP tests start
an isolated local server and check routes, authorization, validation, and rate limits
without connecting to a database or Docker services.

## Running-stack checks

Start the [Docker stack](../README.md#run-with-docker-compose), then try:

```sh
python3 tests/http/verify_catalog_admin_http.py
```

The scripts in `http` call the real API. Browser checks are in
`src/storefront/e2e`; see the [frontend README](../src/storefront/README.md#browser-checks).
API and browser checks retain test data in the local database.

SQL and broker integration checks live in `src/Ecommerce.Api/Verification` and
mostly use disposable databases. `--verify-sqlserver` retains its rows in the
separate `EcommerceVerification` database. See [verification](../docs/verification.md) for all
commands, prerequisites, outage checks, and CI. Some checks stop containers or
restart the API, so use a local test stack.
