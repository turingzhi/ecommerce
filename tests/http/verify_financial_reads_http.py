"""Verify customer-owned payment/refund reads and bounded refund pagination.
Default mode checks Pending payment reads and empty refund history.
--development also checks live refund status changes and populated pagination.
"""

from support import BASE_URL, check, compose, assert_no_store, register_and_login, request, scalar
import argparse
import re
import time
from uuid import uuid4


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--development", action="store_true")
    args = parser.parse_args()
    missing_payment = uuid4()
    missing_refund = uuid4()
    for path in [f"/payments/{missing_payment}", f"/payments/{missing_payment}/refunds", f"/refunds/{missing_refund}"]:
        request("GET", path, expected=401)
    marker = uuid4().hex
    password = "FinancialReads!123456"
    owner = register_and_login(f"reads-owner-{marker}@example.invalid", password)
    other = register_and_login(f"reads-other-{marker}@example.invalid", password)
    for path in [f"/payments/{missing_payment}", f"/payments/{missing_payment}/refunds", f"/refunds/{missing_refund}"]:
        request("GET", path, token=owner, expected=404)
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--create-product",
                     f"Reads {marker}", "Read verification", "Verification", "5000", "2")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Product ID missing")
    product_id = int(match.group(1))
    order = request("POST", "/orders", token=owner, key=uuid4().hex,
                    body={"items": [{"productId": product_id, "quantity": 1}]}, expected=201)
    payment = request("POST", f"/orders/{order['id']}/payments", token=owner, key=uuid4().hex, expected=201)
    payment_path = f"/payments/{payment['id']}"
    history_path = payment_path + "/refunds"
    assert_no_store(payment_path, owner)
    assert_no_store(history_path, owner)
    details = request("GET", payment_path, token=owner)
    differences = {name: (payment.get(name), details.get(name)) for name in payment if payment.get(name) != details.get(name)}
    check(details == payment, f"Payment details differ from creation response: {differences}")
    check(request("GET", history_path, token=owner) == {"page": 1, "pageSize": 10, "refunds": []},
          "New payment refund history not empty")
    request("GET", payment_path, token=other, expected=404)
    request("GET", history_path, token=other, expected=404)
    for _ in range(25):
        request("GET", payment_path, token=owner)
    # Framework parameter binding returns 400; Development may return a text
    # diagnostic page rather than JSON. The contract here is the HTTP status.
    from urllib.error import HTTPError
    from urllib.request import Request, urlopen
    for query in ["?page=invalid", "?pageSize=invalid"]:
        try:
            with urlopen(Request(BASE_URL + history_path + query,
                                 headers={"Authorization": "Bearer " + owner}), timeout=15) as response:
                check(response.status == 400, "Malformed page query accepted")
        except HTTPError as error:
            with error:
                check(error.code == 400, "Malformed page query did not return 400")
    normalized = request("GET", history_path + "?page=0&pageSize=0", token=owner)
    check(normalized == {"page": 1, "pageSize": 1, "refunds": []}, "Pagination minimum not normalized")
    capped = request("GET", history_path + "?pageSize=1000", token=owner)
    check(capped["pageSize"] == 50, "Refund page size exceeds cap")
    huge = request("GET", history_path + "?page=2147483647&pageSize=50", token=owner)
    check(huge["refunds"] == [], "Large page overflowed")
    if args.development:
        request("POST", f"/dev/payments/{payment['id']}/simulate", token=owner, body={"outcome": "success"})
        check(request("GET", payment_path, token=owner)["status"] == "Succeeded", "Payment read cached old status")
        refunds = []
        for index, outcome in enumerate(["failure", "success", "success"]):
            time.sleep(0.02)
            created = request("POST", history_path, token=owner, key=uuid4().hex,
                              body={"amountCents": 1000}, expected=201)
            detail_path = f"/refunds/{created['id']}"
            assert_no_store(detail_path, owner)
            check(request("GET", detail_path, token=owner) == created, "Refund details differ from creation response")
            request("GET", detail_path, token=other, expected=404)
            if index == 1:
                request("POST", f"/dev/refunds/{created['id']}/simulate", token=owner, body={"outcome": "timeout"})
                check(request("GET", detail_path, token=owner)["status"] == "Unknown", "Refund read lost Unknown status")
            resolved = request("POST", f"/dev/refunds/{created['id']}/simulate", token=owner, body={"outcome": outcome})
            check(request("GET", detail_path, token=owner) == resolved, "Refund read cached old status")
            refunds.append(resolved)
        expected_ids = [entry["id"] for entry in reversed(refunds)]
        default = request("GET", history_path, token=owner)
        check(default["refunds"] == list(reversed(refunds)), "Refund history detail/timestamp fields differ")
        check([entry["id"] for entry in default["refunds"]] == expected_ids, "History not newest first")
        check(request("GET", history_path + "?page=2147483647&pageSize=50", token=owner)["refunds"] == [],
              "Large page wrapped into populated history")
        check([entry["status"] for entry in default["refunds"]] == ["Succeeded", "Succeeded", "Failed"],
              "History lost live outcome states")
        observed = []
        for page in [1, 2, 3]:
            result = request("GET", history_path + f"?page={page}&pageSize=1", token=owner)
            check(result["page"] == page and result["pageSize"] == 1, "Pagination metadata incorrect")
            observed.extend(entry["id"] for entry in result["refunds"])
        check(observed == expected_ids, "Pagination duplicated or skipped refunds")
        check(request("GET", history_path + "?page=4&pageSize=1", token=owner)["refunds"] == [],
              "Out-of-range page not empty")
        request("GET", history_path + "?page=2147483647&pageSize=50", token=other, expected=404)
    check(scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}") == 1,
          "Reads modified reserved stock")
    print("Financial read checks passed: auth, ownership, details, empty history, bounds/overflow, no write quota"
          + (", live statuses, newest-first refund pagination" if args.development else ""))


if __name__ == "__main__":
    main()
