"""Verify authenticated refund creation and optional Development-only outcomes.
Default mode verifies auth/validation and that the simulator is absent.
--development creates paid fixture orders and exercises partial refunds and retries.
"""

from support import BASE_URL, check, compose, register_and_login, request, scalar
import argparse
import re
import json
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor
from urllib.request import Request, urlopen


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--development", action="store_true")
    args = parser.parse_args()
    marker = uuid4().hex
    password = "RefundTest!123456"
    missing_payment = uuid4()
    path = f"/payments/{missing_payment}/refunds"
    missing_simulator = f"/dev/refunds/{uuid4()}/simulate"
    request("POST", path, body={"amountCents": 1}, key=marker, expected=401)
    request("POST", missing_simulator, body={"outcome": "success"},
            expected=401 if args.development else 404)
    owner = register_and_login(f"refund-{marker}@example.invalid", password)
    other = register_and_login(f"refund-other-{marker}@example.invalid", password)
    request("POST", path, token=owner, body={"amountCents": 1}, key=marker, expected=404)
    request("POST", missing_simulator, token=owner, body={"outcome": "success"}, expected=404)
    for amount in [0, -1]:
        request("POST", path, token=owner, body={"amountCents": amount}, key=marker, expected=400)
    request("POST", path, token=owner, body={}, key=marker, expected=400)
    for key in [None, "x" * 101]:
        request("POST", path, token=owner, body={"amountCents": 1}, key=key, expected=400)
    if not args.development:
        print("Refund default-environment checks passed: auth, validation, missing payment, simulator absent")
        return

    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--create-product",
                     f"Refund {marker}", "Refund verification", "Verification", "5000", "10")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Product ID missing")
    product_id = int(match.group(1))
    stock = lambda: scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}")
    def paid_fixture(label):
        token = register_and_login(f"refund-{label}-{marker}@example.invalid", password)
        order = request("POST", "/orders", token=token, key=uuid4().hex,
                        body={"items": [{"productId": product_id, "quantity": 1}]}, expected=201)
        payment = request("POST", f"/orders/{order['id']}/payments", token=token,
                          key=uuid4().hex, expected=201)
        # Pending payments must be ineligible before becoming successful.
        request("POST", f"/payments/{payment['id']}/refunds", token=token,
                key=uuid4().hex, body={"amountCents": 1}, expected=409)
        request("POST", f"/dev/payments/{payment['id']}/simulate", token=token,
                body={"outcome": "success"})
        return token, order, payment
    def refund(token, payment, amount, key=None, expected=201):
        return request("POST", f"/payments/{payment['id']}/refunds", token=token,
                       body={"amountCents": amount}, key=key or uuid4().hex, expected=expected)
    def simulate(token, created, outcome, expected=200):
        return request("POST", f"/dev/refunds/{created['id']}/simulate", token=token,
                       body={"outcome": outcome}, expected=expected)
    def events(order, kind):
        return scalar(f"SELECT COUNT(*) FROM dbo.Outbox WHERE OrderId = '{order['id']}' AND Type = '{kind}'")

    token, order, payment = paid_fixture("partial")
    refund(other, payment, 2000, expected=404)
    key = marker + "-partial"
    def contend(_):
        req = Request(BASE_URL + f"/payments/{payment['id']}/refunds", method="POST",
                      headers={"Authorization": "Bearer " + token, "Content-Type": "application/json",
                               "Idempotency-Key": key}, data=json.dumps({"amountCents": 2000}).encode())
        with urlopen(req, timeout=15) as response:
            return response.status, json.load(response)
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(contend, range(2)))
    check(sorted(code for code, _ in outcomes) == [200, 201], "Concurrent same-key refunds did not replay")
    created = outcomes[0][1]
    check(created["id"] == outcomes[1][1]["id"], "Concurrent retry created two refunds")
    check(created["status"] == "Pending" and created["amountCents"] == 2000 and created["currency"] == "EUR",
          "Refund response lost amount/status/currency")
    refund(token, payment, 1000, key=key, expected=409)
    refund(token, payment, 1, expected=409)
    for outcome in ["success", "failure", "timeout"]:
        simulate(other, created, outcome, expected=404)
    for body in [{}, {"outcome": None}, {"outcome": "invalid"}]:
        request("POST", f"/dev/refunds/{created['id']}/simulate", token=other, body=body, expected=400)
    check(simulate(token, created, "timeout")["status"] == "Unknown", "Timeout not unknown")
    simulate(token, created, "timeout")
    refund(token, payment, 1, expected=409)
    check(events(order, "RefundUnknown") == 1, "Timeout retry duplicated event")
    check(simulate(token, created, "success")["status"] == "Succeeded", "Unknown not resolved to success")
    simulate(token, created, "success")
    simulate(token, created, "failure", expected=409)
    refund(token, payment, 3001, expected=409)
    final_key = marker + "-remaining"
    final = refund(token, payment, 3000, key=final_key)
    simulate(token, final, "success")
    refund(token, payment, 1, expected=409)
    check(refund(token, payment, 3000, key=final_key, expected=200)["id"] == final["id"],
          "Refund replay broke after exhausting balance")
    check(events(order, "RefundSucceeded") == 2, "Partial refunds duplicated or omitted events")
    check(request("GET", f"/orders/{order['id']}", token=token)["status"] == "Paid", "Refund changed order status")
    check(stock() == 9, "Refund changed reserved stock")

    token, order, payment = paid_fixture("failed")
    created = refund(token, payment, 2000)
    check(simulate(token, created, "failure")["status"] == "Failed", "Failure not failed")
    simulate(token, created, "failure")
    simulate(token, created, "success", expected=409)
    retry = refund(token, payment, 2000)
    simulate(token, retry, "timeout")
    simulate(token, retry, "failure")
    full = refund(token, payment, 5000)
    simulate(token, full, "success")
    check(events(order, "RefundFailed") == 2 and events(order, "RefundSucceeded") == 1,
          "Failed refunds blocked full refund or duplicated events")
    check(stock() == 8, "Refund outcome restored stock")
    print("Refund Development checks passed: ownership, validation, partial/full refunds, concurrent replay, amount conflicts, unresolved blocking, both Unknown resolutions, repeat-safe events, stock")


if __name__ == "__main__":
    main()
