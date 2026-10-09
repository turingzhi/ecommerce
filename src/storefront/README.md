# Storefront

React + TypeScript with Vite. The interface includes products, accounts, carts,
orders, payment status, refunds, and admin pages.

## Develop

Use Node 22, version 22.12 or newer within that major version.
Start the [Docker stack](../../README.md#run-with-docker-compose) first.
Then run these commands from the repository root:

```sh
npm --prefix src/storefront ci
npm --prefix src/storefront run dev
```

Open [the development UI](http://127.0.0.1:5173/). Vite forwards API requests to
`http://127.0.0.1:5088`. Docker builds the UI and serves it on port 5088 with the API.

## Check and build

From the repository root:

```sh
npm --prefix src/storefront test
npm --prefix src/storefront run build
```

The build checks TypeScript and writes `src/storefront/dist`.
If running tests on Node 25, use
`NODE_OPTIONS=--no-experimental-webstorage npm --prefix src/storefront test`.

## Formatting

Format the frontend source, tests, and configuration from the repository root:

```sh
npm --prefix src/storefront run format
npm --prefix src/storefront run format:check
```

Prettier uses the settings in `.prettierrc.json`. Generated files are excluded.

## Browser checks

With the Compose API running, run from `src/storefront`:

```sh
npx playwright install chromium
npm run test:e2e
```

Browser checks create and retain test accounts, products, and orders.
See [verification](../../docs/verification.md) for Development-mode and telemetry checks.

See [storefront usage](../../docs/storefront.md), [admin access](../../docs/admin.md),
and [demo maintenance](../../tools/demo/README.md) for user flows and local data.
