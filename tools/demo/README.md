# Demo catalog maintenance

Optional tool for replacing retained test-product names with readable demo content.
It uses the running API and Compose catalog CLI.

From the repository root, preview changes:

```sh
python3 tools/demo/refresh_catalog.py
```

Apply the previewed changes:

```sh
python3 tools/demo/refresh_catalog.py --apply
```

The tool backs up original product details under `.superpowers/demo-catalog`,
then updates recognized demo/test products. It preserves product IDs, prices,
stock, and order history. You can rerun it after an interrupted run or new tests.

See [demo data details](../../docs/storefront.md#catalog-presentation-and-local-demo-data).
