"""Verify owner-only asynchronous shipments; --development pays a fresh order."""

from support import check, compose, register_and_login, request, scalar
from fixtures import wait_for_shipment
import argparse
import re
from uuid import UUID, uuid4




def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--development", action="store_true")
    args = parser.parse_args()
    missing = f"/orders/{uuid4()}/shipment"
    request("GET", missing, expected=401)
    marker = uuid4().hex
    owner = register_and_login(f"shipment-owner-{marker}@example.invalid", "Shipment!123456")
    other = register_and_login(f"shipment-other-{marker}@example.invalid", "Shipment!123456")
    request("GET", missing, token=owner, expected=404)
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--create-product",
                     f"Shipment {marker}", "Shipment verification", "Verification", "5000", "2")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Product ID missing")
    product_id = int(match.group(1))
    order = request("POST", "/orders", token=owner, key=uuid4().hex,
                    body={"items": [{"productId": product_id, "quantity": 1}]}, expected=201)
    path = f"/orders/{order['id']}/shipment"
    request("GET", path, token=owner, expected=404)
    request("GET", path, token=other, expected=404)
    if args.development:
        payment_key = uuid4().hex
        payment = request("POST", f"/orders/{order['id']}/payments", token=owner, key=payment_key, expected=201)
        request("POST", f"/dev/payments/{payment['id']}/simulate", token=owner, body={"outcome": "success"})
        shipment = wait_for_shipment(order['id'], owner)
        check(set(shipment) == {"id", "orderId", "status", "createdAt", "trackingNumber", "shippedAt", "deliveredAt"}, "Unexpected shipment fields")
        check(shipment['orderId'] == order['id'] and shipment['status'] == "Pending", "Wrong shipment identity/status")
        check(UUID(shipment['id']).int != 0, "Empty shipment ID")
        check(shipment['createdAt'].endswith("Z"), "Shipment timestamp not UTC")
        request("GET", path, token=other, expected=404)
        for _ in range(25):
            check(request("GET", path, token=owner) == shipment, "Repeated read changed shipment")
        check(request("GET", f"/orders/{order['id']}", token=owner)['status'] == "Paid", "Shipment changed order state")
        check(request("GET", f"/payments/{payment['id']}", token=owner)['status'] == "Succeeded", "Shipment changed payment")
        check(scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}") == 1, "Shipment changed stock")
        # A fresh payment write still succeeds after repeated shipment reads.
        request("POST", f"/orders/{order['id']}/payments", token=owner, key=payment_key)
    else:
        request("POST", f"/orders/{order['id']}/cancel", token=owner)
        check(scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}") == 2, "Cleanup did not restore stock")
    print("Shipment HTTP checks passed" + (": paid event, ownership, UTC, no-store, stable reads, stock" if args.development else ": auth, ownership, pending 404, cleanup"))


if __name__ == "__main__":
    main()
