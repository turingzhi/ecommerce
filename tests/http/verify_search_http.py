"""Verify filtered/paginated search and Redis isolation on the local Compose stack.

Creates three dedicated catalog products and leaves them for inspection.
Run: python3 tests/http/verify_search_http.py
"""

from support import check, compose, request

import re
import time
from urllib.parse import urlencode
from uuid import uuid4



def main():
    # Check the new contract before creating test records.
    response = request("GET", "/products/search?q=wireless")
    check(isinstance(response, dict) and "products" in response,
          "Search must return a paginated response object, not an array")
    check(response["page"] == 1 and response["pageSize"] == 20,
          "Unexpected pagination defaults")

    marker = uuid4().hex
    category_a, category_b = f"A-{marker}", f"B-{marker}"
    ids = []
    for index, (category, price) in enumerate([
        (category_a, 1000), (category_a, 2000), (category_b, 3000)
    ]):
        output = compose(
            "exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
            "--create-product", f"{marker} Product {index}", "Search verification",
            category, str(price), "1",
        )
        match = re.search(r"Saved product (\d+) at version", output)
        check(match is not None, "Catalog CLI did not return a product ID")
        ids.append(int(match.group(1)))

    def search(**params):
        return request("GET", "/products/search?" + urlencode({"q": marker, **params}))

    for _ in range(30):
        if search()["total"] == 3:
            break
        time.sleep(1)
    else:
        raise AssertionError("Dedicated products did not reach search")

    def assert_result(result, expected_ids, total, page=1, size=20):
        check([p["id"] for p in result["products"]] == expected_ids,
              f"Wrong results: expected IDs {expected_ids}, got {result}")
        check(result["total"] == total and result["page"] == page
              and result["pageSize"] == size, "Wrong pagination metadata")

    # Alternate request shapes and repeat each: cached results must stay isolated.
    cases = [
        ({}, ids, 3, 1, 20),
        ({"pageSize": 2}, ids[:2], 3, 1, 2),
        ({"page": 2, "pageSize": 2}, ids[2:], 3, 2, 2),
        ({"page": 3, "pageSize": 2}, [], 3, 3, 2),
        ({"category": category_a}, ids[:2], 2, 1, 20),
        ({"category": category_b}, ids[2:], 1, 1, 20),
        ({"category": "missing-" + marker}, [], 0, 1, 20),
        ({"minPriceCents": 2000}, ids[1:], 2, 1, 20),
        ({"maxPriceCents": 2000}, ids[:2], 2, 1, 20),
        ({"minPriceCents": 2000, "maxPriceCents": 2000}, ids[1:2], 1, 1, 20),
        ({"category": category_a, "minPriceCents": 1500,
          "maxPriceCents": 2500, "pageSize": 1}, ids[1:2], 1, 1, 1),
    ]
    for _ in range(2):
        for params, expected_ids, total, page, size in cases:
            assert_result(search(**params), expected_ids, total, page, size)

    assert_result(search(category="  " + category_a + "  "), ids[:2], 2)
    assert_result(search(category="   "), ids, 3)
    invalid = [
        {"page": 0}, {"page": -1}, {"page": 2147483647},
        {"pageSize": 0}, {"pageSize": 51},
        {"minPriceCents": -1}, {"maxPriceCents": -1},
        {"minPriceCents": 2001, "maxPriceCents": 2000},
        {"category": "x" * 201}, {"page": "abc"},
    ]
    for params in invalid:
        request("GET", "/products/search?" + urlencode({"q": marker, **params}), expected=400)
    for q in ["", "   ", "x" * 201]:
        request("GET", "/products/search?" + urlencode({"q": q}), expected=400)
    assert_result(search(page=200, pageSize=50), [], 3, 200, 50)
    request("GET", "/products/search?" + urlencode({"q": marker, "page": 201, "pageSize": 50}),
            expected=400)

    # A product update must invalidate an already populated filtered cache.
    assert_result(search(maxPriceCents=2000), ids[:2], 2)
    before = compose("exec", "-T", "redis", "redis-cli", "--raw", "GET",
                     "products:search:generation").strip()
    compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
            "--update-product", str(ids[0]), f"{marker} Product 0",
            "Search verification", category_a, "9999")
    for _ in range(10):
        after = compose("exec", "-T", "redis", "redis-cli", "--raw", "GET",
                        "products:search:generation").strip()
        if after != before:
            break
        time.sleep(1)
    else:
        raise AssertionError("Product update did not increment the cache generation")
    assert_result(search(maxPriceCents=2000), ids[1:2], 1)
    assert_result(search(maxPriceCents=2000), ids[1:2], 1)
    print("Search HTTP checks passed: response contract, filters, inclusive prices, "
          "pagination, totals, validation, Redis key isolation, and update freshness")


if __name__ == "__main__":
    main()
