"""Verify admin-only shipment transitions. --development creates and pays an order.
Default mode verifies real status updates on an isolated SQL-seeded shipment,
so the admin route is covered without exposing the payment simulator.
"""

from support import BASE_URL, check, compose, login, register_and_login, request, scalar
from fixtures import wait_for_shipment
import argparse
import json
import re
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from urllib.request import Request, urlopen




def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--development", action="store_true")
    args = parser.parse_args()
    unknown_path = f"/admin/shipments/{uuid4()}/status"
    request("PUT", unknown_path, body={"status": "Shipped", "trackingNumber": "TEST"}, expected=401)
    marker = uuid4().hex
    password = "ShipmentStatus!123456"
    owner_email = f"shipment-status-owner-{marker}@example.invalid"
    admin_email = f"shipment-status-admin-{marker}@example.invalid"
    owner = register_and_login(owner_email, password)
    admin_before_grant = register_and_login(admin_email, password)
    request("PUT", unknown_path, token=owner, body={"status": "Shipped", "trackingNumber": "TEST"}, expected=403)
    request("PUT", unknown_path, token=admin_before_grant, body={"status": "Shipped", "trackingNumber": "TEST"}, expected=403)
    missing_email = f"missing-{marker}@example.invalid"
    import subprocess
    result = subprocess.run(["docker", "compose", "exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--grant-shipment-admin", missing_email],
                            capture_output=True, text=True, timeout=30)
    check(result.returncode != 0, "Admin command accepted nonexistent account")
    for _ in range(2):
        compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--grant-shipment-admin", admin_email)
    check(scalar(f"SELECT COUNT(*) FROM dbo.AspNetUserClaims c JOIN dbo.AspNetUsers u ON u.Id=c.UserId WHERE u.Email='{admin_email}' AND c.ClaimType='permission' AND c.ClaimValue='shipments:manage'") == 1,
          "Repeated admin grant duplicated permission")
    # Bearer tokens contain a snapshot of claims: an existing token cannot gain privilege.
    request("PUT", unknown_path, token=admin_before_grant, body={"status": "Shipped", "trackingNumber": "TEST"}, expected=403)
    admin = login(admin_email, password)
    request("PUT", unknown_path, token=admin, body={"status": "Shipped", "trackingNumber": "TEST"}, expected=404)
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--create-product",
                     f"Shipment status {marker}", "Status verification", "Verification", "5000", "2")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Product ID missing")
    product_id = int(match.group(1))
    order = request("POST", "/orders", token=owner, key=uuid4().hex,
                    body={"items": [{"productId": product_id, "quantity": 1}]}, expected=201)
    payment = request("POST", f"/orders/{order['id']}/payments", token=owner, key=uuid4().hex, expected=201)
    if args.development:
        request("POST", f"/dev/payments/{payment['id']}/simulate", token=owner, body={"outcome": "success"})
        shipment = wait_for_shipment(order['id'], owner)
    else:
        # Test-only fixture setup, never a public simulator. Paid stock is already reserved by checkout.
        shipment_id = uuid4()
        compose("exec", "-T", "sqlserver", "bash", "-lc",
                "SQLCMDPASSWORD=\"$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d Ecommerce -b -Q " +
                "\"SET XACT_ABORT ON; BEGIN TRAN; " +
                f"UPDATE dbo.Payments SET Status=N'Succeeded' WHERE Id='{payment['id']}'; " +
                f"UPDATE dbo.Orders SET Status=N'Paid' WHERE Id='{order['id']}'; " +
                f"INSERT dbo.Shipments (Id,OrderId,Status,CreatedAt) VALUES ('{shipment_id}','{order['id']}',N'Pending',SYSUTCDATETIME()); COMMIT;\"")
        shipment = request("GET", f"/orders/{order['id']}/shipment", token=owner)
    path = f"/admin/shipments/{shipment['id']}/status"
    owner_path = f"/orders/{order['id']}/shipment"
    check(shipment['trackingNumber'] is None and shipment['shippedAt'] is None and shipment['deliveredAt'] is None,
          "Pending shipment has fulfillment details")
    request("PUT", path, token=owner, body={"status": "Shipped", "trackingNumber": "HACK"}, expected=403)
    request("PUT", path, token=admin, body={"status": "Delivered"}, expected=409)
    request("PUT", path, token=admin, body={"status": "Pending"}, expected=409)
    for body in [{}, {"status": "Lost"}, {"status": "Shipped"},
                 {"status": "Shipped", "trackingNumber": "   "}, {"status": "Shipped", "trackingNumber": "x" * 101},
                 {"status": "Shipped", "trackingNumber": "bad\ntracking"}]:
        request("PUT", path, token=admin, body=body, expected=400)
    check(request("GET", owner_path, token=owner) == shipment, "Rejected updates changed shipment")
    shipping = {"status": "Shipped", "trackingNumber": "  TRACK-" + marker + "  "}
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(lambda _: request("PUT", path, token=admin, body=shipping), range(2)))
    check(outcomes[0] == outcomes[1], "Concurrent shipping replay changed response")
    shipped = outcomes[0]
    request("PUT", path, token=admin, body={"status": "Pending"}, expected=409)
    check(shipped['status'] == "Shipped" and shipped['trackingNumber'] == "TRACK-" + marker and
          shipped['shippedAt'].endswith("Z") and shipped['deliveredAt'] is None, "Shipped response invalid")
    check(request("GET", owner_path, token=owner) == shipped, "Owner read did not update to Shipped")
    request("PUT", path, token=admin, body={"status": "Shipped", "trackingNumber": "OTHER"}, expected=409)
    request("PUT", path, token=admin, body={"status": "Delivered", "trackingNumber": "OTHER"}, expected=409)
    delivered = request("PUT", path, token=admin, body={"status": "Delivered"})
    check(delivered['status'] == "Delivered" and delivered['shippedAt'] == shipped['shippedAt'] and
          delivered['deliveredAt'].endswith("Z") and delivered['trackingNumber'] == shipped['trackingNumber'], "Delivered response invalid")
    check(request("PUT", path, token=admin, body={"status": "Delivered"}) == delivered, "Delivery replay changed timestamps")
    request("PUT", path, token=admin, body=shipping, expected=409)
    request("PUT", path, token=admin, body={"status": "Pending"}, expected=409)
    check(request("GET", owner_path, token=owner) == delivered, "Owner read did not update to Delivered")
    request("GET", owner_path, token=admin, expected=404)
    with urlopen(Request(BASE_URL + path, method="PUT", data=json.dumps({"status": "Delivered"}).encode(),
                         headers={"Authorization": "Bearer " + admin, "Content-Type": "application/json"}), timeout=15) as response:
        check(response.headers.get("Cache-Control") == "no-store", "Admin response is cacheable")
    check(request("GET", f"/orders/{order['id']}", token=owner)['status'] == "Paid", "Shipment status changed order")
    check(request("GET", f"/payments/{payment['id']}", token=owner)['status'] == "Succeeded", "Shipment status changed payment")
    check(scalar(f"SELECT Available FROM dbo.Products WHERE Id={product_id}") == 1, "Shipment status changed stock")
    print("Shipment status HTTP passed: auth, explicit admin grant, frozen claims, validation, concurrency, replay, tracking, UTC, owner reads, no-store, stock")


if __name__ == "__main__":
    main()
