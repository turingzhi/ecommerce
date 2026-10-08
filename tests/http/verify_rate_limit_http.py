"""Verify default rate limits on the local API without reserving stock.

Run after other HTTP checks: fills the search IP bucket and two customer buckets.
Creates two accounts. Defaults match src/Ecommerce.Api/appsettings.json; use a freshly started API.
"""

from support import BASE_URL, check, register_and_login

import json
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from uuid import uuid4



def send(method, path, token=None, body=None, forwarded_ip=None):
    headers = {}
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        headers["Content-Type"] = "application/json"
    if forwarded_ip:
        headers["X-Forwarded-For"] = forwarded_ip
    request = Request(BASE_URL + path, method=method, headers=headers,
                      data=json.dumps(body).encode() if body is not None else None)
    try:
        response = urlopen(request, timeout=15)
    except HTTPError as error:
        response = error
    with response:
        content = response.read()
        return response.status, response.headers, json.loads(content) if content else None


def exhaust(method, path, expected, budget, token=None, body=None):
    for _ in range(budget + 1):
        status, headers, content = send(method, path, token, body)
        if status == 429:
            check(headers.get("Retry-After", "").isdigit(), "429 has no numeric Retry-After")
            check(int(headers["Retry-After"]) >= 1, "Retry-After must be positive")
            check(content == {"error": "Too many requests. Please retry later."},
                  "Unexpected throttle error response")
            return int(headers["Retry-After"])
        check(status == expected, f"Unexpected status {status} before throttling")
    raise AssertionError(f"{path} did not throttle within {budget + 1} requests")


def main():
    # Invalid requests count but do not invoke search or mutate commerce data.
    retry_after = exhaust("GET", "/products/search", 400, 120)
    check(send("GET", "/products/search", forwarded_ip="203.0.113.1")[0] == 429,
          "Untrusted forwarded IP bypassed the search limiter")
    check(send("GET", "/health/live")[0] == 200, "Search limiter blocked liveness")
    print("PASS: search throttles by client IP, rejects spoofed forwarding, and returns Retry-After", flush=True)

    marker = uuid4().hex
    a = register_and_login(f"rate-a-{marker}@example.invalid", "RateTest!123456")
    b = register_and_login(f"rate-b-{marker}@example.invalid", "RateTest!123456")
    exhaust("POST", "/orders", 400, 30, a, {"items": []})
    check(send("POST", "/orders", b, {"items": []})[0] == 400,
          "One customer's orders exhausted another customer's quota")
    check(send("POST", "/orders", body={"items": []})[0] == 401,
          "Anonymous order requests no longer return 401")

    payment_path = f"/orders/{uuid4()}/payments"
    check(send("POST", payment_path, a)[0] == 400,
          "Order quota also exhausted payment quota")
    exhaust("POST", payment_path, 400, 20, a)
    check(send("POST", payment_path, b)[0] == 400,
          "One customer's payments exhausted another customer's quota")
    check(send("POST", payment_path)[0] == 401, "Anonymous payment no longer returns 401")
    check(send("GET", "/orders", a)[0] == 200, "Creation limiter blocked order reading")
    check(send("POST", f"/orders/{uuid4()}/cancel", a)[0] == 404,
          "Creation limiter blocked cancellation")
    print("PASS: separate per-customer order/payment quotas preserve auth, reads, and cancellation", flush=True)

    # Wait for the remaining search window, then demonstrate automatic recovery.
    remaining = send("GET", "/products/search")[1].get("Retry-After")
    if remaining:
        print(f"Waiting {remaining} seconds for search quota recovery", flush=True)
        time.sleep(int(remaining) + 1)
    check(send("GET", "/products/search")[0] == 400, "Search quota did not recover")
    print("Rate-limit HTTP checks passed: rejection, Retry-After, partition isolation, and recovery", flush=True)


if __name__ == "__main__":
    main()
