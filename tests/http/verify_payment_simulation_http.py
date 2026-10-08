"""Verify development payment simulation; --disabled expects no route outside Development.
Creates dedicated accounts/products/orders. Successful simulations do not charge money.
"""

from support import check, compose, register_and_login, request, scalar
import argparse
import re
from uuid import uuid4


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--disabled", action="store_true")
    args = parser.parse_args()
    missing = f"/dev/payments/{uuid4()}/simulate"
    request("POST", missing, body={"outcome": "success"}, expected=404 if args.disabled else 401)
    marker = uuid4().hex
    password = "SimulationTest!123456"
    owner = register_and_login(f"sim-owner-{marker}@example.invalid", password)
    request("POST", missing, token=owner, body={"outcome": "success"}, expected=404)
    if args.disabled:
        print("Payment simulation is unavailable outside Development (anonymous and authenticated)")
        return
    other = register_and_login(f"sim-other-{marker}@example.invalid", password)
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
                     "--create-product", f"Simulator {marker}", "Payment verification", "Verification", "1800", "20")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Product ID missing")
    product_id = int(match.group(1))
    stock = lambda: scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}")
    def order_for(token):
        return request("POST", "/orders", token=token, key=uuid4().hex,
                       body={"items": [{"productId": product_id, "quantity": 1}]}, expected=201)
    def payment_for(token, order):
        return request("POST", f"/orders/{order['id']}/payments", token=token,
                       key=uuid4().hex, expected=201)
    def simulate(token, payment, outcome, expected=200):
        return request("POST", f"/dev/payments/{payment['id']}/simulate", token=token,
                       body={"outcome": outcome}, expected=expected)
    def status(token, order):
        return request("GET", f"/orders/{order['id']}", token=token)["status"]
    def events(order, kind):
        return scalar(f"SELECT COUNT(*) FROM dbo.Outbox WHERE OrderId = '{order['id']}' AND Type = '{kind}'")

    order = order_for(owner)
    payment = payment_for(owner, order)
    for outcome in ["success", "failure", "timeout"]:
        simulate(other, payment, outcome, expected=404)
    check(status(owner, order) == "PendingPayment", "Other customer changed order")
    check(scalar(f"SELECT COUNT(*) FROM dbo.Payments WHERE Id = '{payment['id']}' AND Status = 'Pending'") == 1,
          "Other customer changed payment")
    for body in [{}, {"outcome": None}, {"outcome": ""}, {"outcome": "invalid"}]:
        request("POST", f"/dev/payments/{payment['id']}/simulate", token=owner, body=body, expected=400)
    result = simulate(owner, payment, "success")
    check(result["id"] == payment["id"] and result["status"] == "Succeeded", "Payment not succeeded")
    check(result["amountCents"] == 1800, "Saved payment amount changed")
    check(status(owner, order) == "Paid", "Success did not pay order")
    simulate(owner, payment, "success")
    simulate(owner, payment, "failure", expected=409)
    simulate(owner, payment, "timeout", expected=409)
    check(events(order, "OrderPaid") == 1, "Success retries duplicated OrderPaid")
    request("POST", f"/orders/{order['id']}/cancel", token=owner, expected=409)

    # Fresh customer keeps this scenario independent of the per-customer payment quota.
    token = register_and_login(f"sim-failure-{marker}@example.invalid", password)
    order = order_for(token)
    payment = payment_for(token, order)
    check(simulate(token, payment, "failure")["status"] == "Failed", "Payment not failed")
    simulate(token, payment, "failure")
    simulate(token, payment, "success", expected=409)
    check(status(token, order) == "PendingPayment", "Failure changed order status")
    check(events(order, "PaymentFailed") == 1, "Failure retries duplicated event")
    payment = payment_for(token, order)
    check(simulate(token, payment, "timeout")["status"] == "Unknown", "Timeout not unknown")
    simulate(token, payment, "timeout")
    request("POST", f"/orders/{order['id']}/payments", token=token, key=uuid4().hex, expected=409)
    request("POST", f"/orders/{order['id']}/cancel", token=token, expected=409)
    check(simulate(token, payment, "failure")["status"] == "Failed", "Unknown cannot resolve to failure")
    check(events(order, "PaymentFailed") == 2, "Unknown resolution omitted event")
    request("POST", f"/orders/{order['id']}/cancel", token=token)
    check(stock() == 19, "Failure cancellation did not restore stock")

    token = register_and_login(f"sim-timeout-{marker}@example.invalid", password)
    order = order_for(token)
    payment = payment_for(token, order)
    simulate(token, payment, "timeout")
    check(events(order, "PaymentFailed") == 0 and events(order, "OrderPaid") == 0,
          "Timeout emitted a final outcome event")
    check(status(token, order) == "PendingPayment", "Timeout changed order status")
    check(simulate(token, payment, "success")["status"] == "Succeeded", "Unknown cannot resolve to success")
    simulate(token, payment, "success")
    check(status(token, order) == "Paid" and events(order, "OrderPaid") == 1, "Timeout resolution was not atomic/repeat-safe")
    check(stock() == 18, "Simulation modified reserved stock")
    print("Payment simulator checks passed: auth, ownership, validation, success/failure/timeout, both Unknown resolutions, repeat safety, conflicts, Outbox events, stock")


if __name__ == "__main__":
    main()
