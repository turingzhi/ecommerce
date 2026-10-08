"""Verify cancellation over HTTP against the local Compose stack.

Creates a dedicated product and fresh accounts. Requires Docker and a running API.
Run: python3 tests/http/verify_cancellation_http.py
"""

from support import check, compose, register_and_login, request, scalar

import re
from uuid import UUID, uuid4







def main():
    run_id = uuid4().hex
    missing_path = f"/orders/{uuid4()}/cancel"
    request("POST", missing_path, expected=401)

    password = "CancellationTest!123456"
    owner = register_and_login(f"cancel-owner-{run_id}@example.invalid", password)
    other = register_and_login(f"cancel-other-{run_id}@example.invalid", password)
    request("POST", missing_path, token=owner, expected=404)

    output = compose(
        "exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
        "--create-product", f"Cancellation test {run_id}",
        "Dedicated verification product", "Verification", "1234", "3",
    )
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Catalog CLI did not return a product ID")
    product_id = int(match.group(1))

    def stock():
        return scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}")

    order = request(
        "POST", "/orders", token=owner, key=f"cancel-{run_id}", expected=201,
        body={"items": [{"productId": product_id, "quantity": 2}]},
    )
    order_id = str(UUID(order["id"]))
    path = f"/orders/{order_id}/cancel"
    check(stock() == 1, "Order did not reserve two units")
    request("POST", path, token=other, expected=404)
    check(stock() == 1, "Other customer changed stock")
    check(request("GET", f"/orders/{order_id}", token=owner)["status"] == "PendingPayment",
          "Other customer changed the order")

    for _ in range(2):
        cancelled = request("POST", path, token=owner)
        check(cancelled["id"] == order_id and cancelled["status"] == "Cancelled",
              "Cancellation did not return the cancelled order")
        check("customerId" not in cancelled and "idempotencyKey" not in cancelled,
              "Cancellation leaked internal order fields")
        check(stock() == 3, "Cancellation retry restored stock more than once")
    check(scalar(f"SELECT COUNT(*) FROM dbo.Outbox WHERE OrderId = '{order_id}' "
                 "AND Type = 'OrderCancelled'") == 1,
          "Cancellation produced duplicate events")
    check(request("GET", f"/orders/{order_id}", token=owner)["status"] == "Cancelled",
          "Cancellation was not persisted")

    blocked = request(
        "POST", "/orders", token=owner, key=f"blocked-{run_id}", expected=201,
        body={"items": [{"productId": product_id, "quantity": 1}]},
    )
    blocked_id = blocked["id"]
    request("POST", f"/orders/{blocked_id}/payments", token=owner,
            key=f"payment-{run_id}", expected=201)
    request("POST", f"/orders/{blocked_id}/cancel", token=owner, expected=409)
    check(stock() == 2, "Blocked cancellation changed stock")
    check(request("GET", f"/orders/{blocked_id}", token=owner)["status"] == "PendingPayment",
          "Blocked cancellation changed the order status")
    print("Cancellation HTTP checks passed: authentication, ownership, missing order, "
          "cancellation, replay, stock, one event, and pending payment restriction")


if __name__ == "__main__":
    main()
