"""Verify cart checkout and retry safety against the local Compose stack."""

from support import BASE_URL, check, compose, register_and_login, request, scalar
import argparse
import re
import time
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--outages", action="store_true")
    args = parser.parse_args()
    request("POST", "/cart/checkout", key="anonymous", expected=401)
    marker = uuid4().hex
    token = register_and_login(f"checkout-{marker}@example.invalid", "CheckoutTest!123456")
    other = register_and_login(f"checkout-other-{marker}@example.invalid", "CheckoutTest!123456")
    def checkout(key=None, expected=200):
        return request("POST", "/cart/checkout", token=token, key=key, expected=expected)
    checkout(expected=400)
    checkout("x" * 101, expected=400)
    checkout(marker + "-empty", expected=400)
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
                     "--create-product", f"Checkout {marker}", "Checkout verification", "Verification", "1500", "5")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Product ID missing")
    product_id = int(match.group(1))
    path = f"/cart/items/{product_id}"
    stock = lambda: scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}")
    request("PUT", path, token=token, body={"quantity": 6}, expected=204)
    key = marker + "-order"
    checkout(key, expected=409)
    check(stock() == 5, "Failed checkout changed stock")
    check(request("GET", "/cart", token=token)["items"][0]["quantity"] == 6,
          "Failed checkout changed cart")
    request("PUT", path, token=token, body={"quantity": 2}, expected=204)
    compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--update-product",
            str(product_id), f"Checkout {marker}", "Updated price", "Verification", "1800")
    def contender(_):
        req = Request(BASE_URL + "/cart/checkout", method="POST", headers={
            "Authorization": "Bearer " + token, "Idempotency-Key": key})
        import json
        with urlopen(req, timeout=15) as response:
            return response.status, json.load(response)
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(contender, range(2)))
    check(sorted(status for status, _ in outcomes) == [200, 201], "Concurrent checkout did not replay")
    order = outcomes[0][1]
    check(outcomes[1][1]["id"] == order["id"], "Concurrent checkout created two orders")
    check(order["status"] == "PendingPayment", "Order is not pending payment")
    check(order["orderItems"][0]["quantity"] == 2, "Cart quantity not used")
    check(order["orderItems"][0]["unitPriceCents"] == 1800, "Checkout did not use current SQL price")
    check(stock() == 3, "Stock reserved more than once")
    check(request("GET", "/cart", token=token)["items"][0]["quantity"] == 2,
          "Checkout cleared the cart")
    request("PUT", path, token=token, body={"quantity": 1}, expected=204)
    check(checkout(key)["id"] == order["id"], "Changed cart broke checkout retry")
    request("DELETE", "/cart", token=token, expected=204)
    check(checkout(key)["id"] == order["id"], "Empty cart broke checkout retry")
    request("POST", "/cart/checkout", token=other, key=key, expected=400)
    check(stock() == 3, "Replay changed stock")
    # Other customer's first empty-cart request consumed one permit. Exhaust
    # the remaining 29, then ensure direct orders share the same allowance.
    for index in range(29):
        request("POST", "/cart/checkout", token=other,
                key=f"{marker}-limit-{index}", expected=400)
    request("POST", "/orders", token=other, key=marker + "-limited",
            body={"items": [{"productId": product_id, "quantity": 1}]}, expected=429)
    check(stock() == 3, "Rate-limited checkout changed stock")
    if args.outages:
        try:
            compose("stop", "redis")
            check(checkout(key)["id"] == order["id"], "Redis outage broke SQL replay")
            checkout(marker + "-outage", expected=503)
        finally:
            compose("start", "redis")
            for _ in range(30):
                try:
                    request("GET", "/cart", token=token)
                    break
                except AssertionError:
                    time.sleep(1)
            else:
                raise AssertionError("Redis cart did not recover")
    request("POST", f"/orders/{order['id']}/cancel", token=token)
    check(stock() == 5, "Cancelling checkout order did not restore stock")
    print("Cart checkout checks passed: auth, validation, SQL prices/stock, concurrent retries, changed/empty cart replay, ownership, shared rate limit"
          + (", Redis outage/recovery" if args.outages else ""))


if __name__ == "__main__":
    main()
