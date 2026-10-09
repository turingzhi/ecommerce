"""Verify independent operator reads, cross-customer visibility and customer isolation."""
from uuid import uuid4
import re
from support import check, compose, login, register_and_login, request, scalar, assert_no_store


def main():
    missing = str(uuid4())
    paths = ["/admin/orders", "/admin/payments", f"/admin/orders/{missing}", f"/admin/payments/{missing}"]
    for path in paths:
        request("GET", path, expected=401)
    marker = uuid4().hex
    password = "AdminReads!123456"
    email = f"admin-reads-{marker}@example.invalid"
    stale = register_and_login(email, password)
    other = register_and_login(f"admin-other-{marker}@example.invalid", password)
    for path in paths:
        request("GET", path, token=stale, expected=403)
    for flag, claim in [("order", "orders:read"), ("payment", "payments:read")]:
        for _ in range(2):
            compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", f"--grant-{flag}-reader", email)
        check(scalar(f"SELECT COUNT(*) FROM dbo.AspNetUserClaims c JOIN dbo.AspNetUsers u ON u.Id=c.UserId WHERE u.Email='{email}' AND c.ClaimType='permission' AND c.ClaimValue='{claim}'") == 1, "Duplicate reader claim")
        token = login(email, password)
        if flag == "order":
            request("GET", "/admin/orders", token=token)
            request("GET", "/admin/payments", token=token, expected=403)
    admin = login(email, password)
    for path in paths:
        request("GET", path, token=stale, expected=403)
    # A payment reader does not inherit order or catalog management permissions.
    reader_email = f"payment-reader-{marker}@example.invalid"
    register_and_login(reader_email, password)
    compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--grant-payment-reader", reader_email)
    reader = login(reader_email, password)
    request("GET", "/admin/payments", token=reader)
    request("GET", "/admin/orders", token=reader, expected=403)
    for path in ["/admin/products"]:
        request("GET", path, token=admin, expected=403)
    # Existing catalog permission grants no new read permission.
    compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--grant-product-admin", reader_email)
    catalog_reader = login(reader_email, password)
    request("GET", "/admin/orders", token=catalog_reader, expected=403)
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--create-product", "Admin read fixture " + marker, "Operator read verification", "Verification", "1234", "10")
    product = int(re.search(r"Saved product (\d+) at version", output).group(1))
    orders = []
    payments = []
    for owner in [stale, other]:
        order = request("POST", "/orders", token=owner, key=uuid4().hex, body={"items": [{"productId": product, "quantity": 2}]}, expected=201)
        orders.append(order)
        payments.append(request("POST", f"/orders/{order['id']}/payments", token=owner, key=uuid4().hex, expected=201))
    cancelled = request("POST", "/orders", token=other, key=uuid4().hex, body={"items": [{"productId": product, "quantity": 1}]}, expected=201)
    request("POST", f"/orders/{cancelled['id']}/cancel", token=other)
    listed_orders = request("GET", "/admin/orders?pageSize=50", token=admin)
    check({o['id'] for o in orders} <= {o['id'] for o in listed_orders['orders']}, "Admin missed cross-customer orders")
    listed_payments = request("GET", "/admin/payments?pageSize=50", token=admin)
    check({p['id'] for p in payments} <= {p['id'] for p in listed_payments['payments']}, "Admin missed cross-customer payments")
    for collection in [listed_orders['orders'], listed_payments['payments']]:
        dates = [row['createdAt'] for row in collection]
        check(dates == sorted(dates, reverse=True), "Admin list is not newest first")
        check(all(row['createdAt'].endswith('Z') for row in collection), "Admin timestamps are not UTC")
    for order, payment in zip(orders, payments):
        order_path = f"/admin/orders/{order['id']}"
        payment_path = f"/admin/payments/{payment['id']}"
        detail = request("GET", order_path, token=admin)
        check(detail['orderItems'] == order['orderItems'] and detail['status'] == 'PendingPayment', "Admin order detail changed items/status")
        check(detail['customerId'], "Admin order omits owner")
        pd = request("GET", payment_path, token=admin)
        check(pd['customerId'] == detail['customerId'] and pd['amountCents'] == 2468 and pd['orderId'] == order['id'], "Admin payment detail incorrect")
        selected = request("GET", f"/admin/payments?orderId={order['id']}", token=admin)
        check([p['id'] for p in selected['payments']] == [payment['id']], "Payment order filter leaked other orders")
        assert_no_store(order_path, admin)
        assert_no_store(payment_path, admin)
        request("GET", order_path, token=other, expected=403)
        request("GET", payment_path, token=other, expected=403)
    # New admin permission must never broaden the existing customer endpoints.
    request("GET", f"/orders/{orders[1]['id']}", token=admin, expected=404)
    request("GET", f"/payments/{payments[1]['id']}", token=admin, expected=404)
    request("GET", f"/orders/{orders[1]['id']}/payments", token=admin, expected=404)
    for resource, field, status in [("orders", "orders", "Cancelled"), ("payments", "payments", "Pending")]:
        path = f"/admin/{resource}"
        assert_no_store(path, admin)
        filtered = request("GET", path + f"?status={status}", token=admin)
        check(filtered[field] and all(row['status'] == status for row in filtered[field]), "Status filter ignored")
        request("GET", path + "?status=invalid", token=admin, expected=400)
        request("GET", path + f"?status={status.lower()}", token=admin, expected=400)
        minimum = request("GET", path + "?page=0&pageSize=0", token=admin)
        check(minimum['page'] == 1 and minimum['pageSize'] == 1 and len(minimum[field]) == 1, "Pagination minima incorrect")
        check(request("GET", path + "?pageSize=1000", token=admin)['pageSize'] == 50, "Page size cap ignored")
        huge = request("GET", path + "?page=2147483647&pageSize=50", token=admin)
        check(huge[field] == [] and huge['total'] > 0, "Extreme page overflowed or lost total")
        first = request("GET", path + "?page=1&pageSize=1", token=admin)[field][0]['id']
        second = request("GET", path + "?page=2&pageSize=1", token=admin)[field][0]['id']
        check(first != second, "Pagination repeated first entry")
        request("GET", path + f"/{missing}", token=admin, expected=404)
    check(request("GET", f"/admin/payments?orderId={missing}", token=admin)['payments'] == [], "Missing order filter not empty")
    check(scalar(f"SELECT Available FROM dbo.Products WHERE Id={product}") == 6, "Admin reads modified inventory")
    print("Admin reads passed: independent claims, stale tokens, cross-customer details, filters, pagination, UTC, no-store, unchanged customer ownership")

if __name__ == "__main__":
    main()
