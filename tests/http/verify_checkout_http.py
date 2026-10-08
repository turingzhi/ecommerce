"""Exercise the public checkout flow against a running API.

Run after `docker compose up -d --build --wait` with `python3 tests/http/verify_checkout_http.py`.
Uses a fresh account and idempotency keys on every run.
"""

from support import check, register_and_login, request

from urllib.parse import quote
from uuid import uuid4










def main():
    run_id = uuid4().hex
    password = "HttpSmoke!123456"

    request("GET", "/orders", expected=401)
    products = request("GET", "/products/search?q=" + quote("Wireless Mouse"))["products"]
    mouse = next((item for item in products if item["name"] == "Wireless Mouse"), None)
    check(mouse is not None, "Seeded Wireless Mouse is missing from search")
    check(mouse["priceCents"] == 2000, "Seeded product price changed")

    token = register_and_login(f"http-smoke-{run_id}@example.invalid", password)
    auth = {"token": token}
    order_key = f"http-order-{run_id}"
    order_body = {"items": [{"productId": mouse["id"], "quantity": 1}]}

    order = request("POST", "/orders", body=order_body, key=order_key, expected=201, **auth)
    order_id = order["id"]
    check(order["status"] == "PendingPayment", "New order is not pending payment")
    check(len(order["orderItems"]) == 1, "New order has an unexpected item count")
    check(order["orderItems"][0]["unitPriceCents"] == 2000, "Order did not save the product price")

    replay = request("POST", "/orders", body=order_body, key=order_key, **auth)
    check(replay["id"] == order_id, "Order retry created a second order")
    changed_body = {"items": [{"productId": mouse["id"], "quantity": 2}]}
    request("POST", "/orders", body=changed_body, key=order_key, expected=409, **auth)

    detail = request("GET", f"/orders/{order_id}", **auth)
    check(detail["id"] == order_id, "Order detail returned the wrong order")
    listing = request("GET", "/orders", **auth)
    check(any(item["id"] == order_id for item in listing["orders"]), "Order is missing from list")

    other_token = register_and_login(f"http-other-{run_id}@example.invalid", password)
    request("GET", f"/orders/{order_id}", token=other_token, expected=404)

    payment_path = f"/orders/{order_id}/payments"
    payment_key = f"http-payment-{run_id}"
    payment = request("POST", payment_path, token=token, key=payment_key, expected=201)
    check(payment["orderId"] == order_id, "Payment belongs to a different order")
    check(payment["amountCents"] == 2000, "Payment amount differs from saved order price")
    check(payment["status"] == "Pending", "New payment attempt is not pending")
    payment_replay = request("POST", payment_path, token=token, key=payment_key)
    check(payment_replay["id"] == payment["id"], "Payment retry created a second attempt")
    request("POST", payment_path, token=token, key=f"other-{run_id}", expected=409)

    print("HTTP flow passed: search, auth, owner isolation, order replay, and payment replay")


if __name__ == "__main__":
    main()
