"""Verify authenticated Redis carts on the local Compose stack.

Creates dedicated products/accounts, temporarily shortens one test cart TTL,
and injects a 100-field cart fixture. --outages briefly stops/restores Redis.
"""

from support import BASE_URL, check, compose, register_and_login, request, scalar

import argparse
import re
import time
from uuid import UUID, uuid4
from concurrent.futures import ThreadPoolExecutor
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import json



def redis(*args):
    return compose("exec", "-T", "redis", "redis-cli", "--raw", *map(str, args)).strip()


def cart_key(email):
    # Inspect only this test's account; password remains inside the SQL container.
    user_id = compose(
        "exec", "-T", "sqlserver", "sh", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd '
        '-S localhost -U sa -C -d Ecommerce -b -h -1 -W -Q "$1"',
        "cart-user", f"SET NOCOUNT ON; SELECT Id FROM dbo.AspNetUsers WHERE Email = '{email}'",
    ).strip()
    return "cart:v1:" + str(UUID(user_id))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--outages", action="store_true")
    args = parser.parse_args()
    request("GET", "/cart", expected=401)
    request("PUT", "/cart/items/1", body={"quantity": 1}, expected=401)
    request("DELETE", "/cart/items/1", expected=401)
    request("DELETE", "/cart", expected=401)

    marker = uuid4().hex
    password = "CartTest!123456"
    email = f"cart-a-{marker}@example.invalid"
    owner = register_and_login(email, password)
    other = register_and_login(f"cart-b-{marker}@example.invalid", password)
    key = cart_key(email)
    request("GET", "/cart", token=owner)
    check(request("GET", "/cart", token=owner) == {"items": []}, "New cart is not empty")
    check(redis("EXISTS", key) == "0", "Empty read creates a Redis key")

    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
                     "--create-product", f"Cart test {marker}", "Cart verification",
                     "Verification", "1500", "5")
    match = re.search(r"Saved product (\d+) at version", output)
    check(match is not None, "Catalog CLI did not return product ID")
    product_id = int(match.group(1))
    output = compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
                     "--create-product", f"Cart second {marker}", "Cart verification",
                     "Verification", "2500", "1")
    second_match = re.search(r"Saved product (\d+) at version", output)
    check(second_match is not None, "Second catalog product ID is missing")
    second_id = int(second_match.group(1))
    path = f"/cart/items/{product_id}"
    stock = lambda: scalar(f"SELECT Available FROM dbo.Products WHERE Id = {product_id}")

    def get_cart():
        return request("GET", "/cart", token=owner)

    for _ in range(2):
        request("PUT", path, token=owner, body={"quantity": 2}, expected=204)
        check(get_cart() == {"items": [{"productId": product_id, "quantity": 2}]},
              "Repeating PUT increases quantity rather than setting it")
    check(redis("TYPE", key) == "hash", "Cart is not stored as a Redis hash")
    check(redis("HGET", key, product_id) == "2", "Hash field does not store quantity")
    check(604790 <= int(redis("TTL", key)) <= 604800, "Cart does not expire in seven days")
    check(stock() == 5, "Adding a cart item reserves stock")
    check(request("GET", "/cart", token=other) == {"items": []}, "Cart leaks to another customer")
    request("DELETE", path, token=other, expected=204)
    check(get_cart()["items"][0]["quantity"] == 2, "Other customer deleted owner's cart item")

    for quantity in [0, -1, 101]:
        request("PUT", path, token=owner, body={"quantity": quantity}, expected=400)
    request("PUT", "/cart/items/0", token=owner, body={"quantity": 1}, expected=400)
    request("DELETE", "/cart/items/0", token=owner, expected=400)
    request("PUT", "/cart/items/2147483647", token=owner, body={"quantity": 1}, expected=404)
    check(get_cart()["items"][0]["quantity"] == 2, "Rejected write changed cart")

    # Reads and writes refresh inactivity; invalid writes do not.
    redis("EXPIRE", key, 120)
    request("PUT", path, token=owner, body={"quantity": 0}, expected=400)
    check(0 < int(redis("TTL", key)) <= 120, "Invalid write refreshed expiry")
    get_cart()
    check(int(redis("TTL", key)) > 604790, "Cart read does not refresh expiry")
    redis("EXPIRE", key, 120)
    request("PUT", path, token=owner, body={"quantity": 6}, expected=204)
    check(int(redis("TTL", key)) > 604790, "Cart update does not refresh expiry")
    check(stock() == 5, "Cart quantity above stock changes inventory")

    # Checkout validates against SQL, not the cart's quantity or cached catalog.
    request("POST", "/orders", token=owner, key=f"cart-too-many-{marker}",
            body=get_cart(), expected=409)
    check(stock() == 5, "Failed checkout changed inventory")
    compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll",
            "--update-product", str(product_id), f"Cart test {marker}",
            "Cart verification", "Verification", "1800")
    request("PUT", path, token=owner, body={"quantity": 3}, expected=204)
    body = get_cart()
    order = request("POST", "/orders", token=owner, key=f"cart-order-{marker}", body=body, expected=201)
    check(order["orderItems"][0]["unitPriceCents"] == 1800, "Checkout did not use the changed SQL price")
    check(stock() == 2, "Checkout did not reserve cart quantity")
    replay = request("POST", "/orders", token=owner, key=f"cart-order-{marker}", body=body)
    check(replay["id"] == order["id"] and stock() == 2, "Checkout retry reserved stock twice")
    check(get_cart() == body, "Checkout unexpectedly cleared the cart")
    request("POST", f"/orders/{order['id']}/cancel", token=owner)
    check(stock() == 5, "Test order cancellation did not restore stock")

    request("DELETE", path, token=owner, expected=204)
    request("DELETE", path, token=owner, expected=204)
    check(get_cart() == {"items": []} and redis("EXISTS", key) == "0", "Last item removal leaves a cart")

    # Populate an isolated fixture to check the atomic distinct-item cap.
    fields = [value for number in range(1000000, 1000100) for value in (str(number), "1")]
    redis("HSET", key, *fields)
    redis("EXPIRE", key, 120)
    request("PUT", path, token=owner, body={"quantity": 1}, expected=409)
    check(redis("HLEN", key) == "100", "Full cart accepted another product")
    request("DELETE", "/cart/items/1000000", token=owner, expected=204)
    check(int(redis("TTL", key)) > 604790, "Successful removal did not refresh expiry")
    def contend(identifier):
        attempt = Request(BASE_URL + f"/cart/items/{identifier}", method="PUT",
                          headers={"Authorization": "Bearer " + owner,
                                   "Content-Type": "application/json"},
                          data=json.dumps({"quantity": 1}).encode())
        try:
            with urlopen(attempt, timeout=15) as response:
                return identifier, response.status
        except HTTPError as error:
            with error:
                return identifier, error.code
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(contend, [product_id, second_id]))
    check(sorted(status for _, status in outcomes) == [204, 409],
          "Concurrent distinct additions exceeded the 100-item cap")
    winner = next(identifier for identifier, status in outcomes if status == 204)
    request("PUT", f"/cart/items/{winner}", token=owner, body={"quantity": 100}, expected=204)
    request("PUT", f"/cart/items/{winner}", token=owner, body={"quantity": 99}, expected=204)
    check(redis("HLEN", key) == "100", "Existing item update broke cart cap")
    request("DELETE", "/cart", token=owner, expected=204)
    request("DELETE", "/cart", token=owner, expected=204)
    check(get_cart() == {"items": []}, "Clear/repeated clear did not empty cart")

    request("PUT", path, token=owner, body={"quantity": 1}, expected=204)
    redis("EXPIRE", key, 1)
    time.sleep(1.2)
    check(get_cart() == {"items": []}, "Expired cart is not empty")

    if args.outages:
        try:
            compose("stop", "redis")
            failure = request("GET", "/cart", token=owner, expected=503)
            check(failure == {"error": "Shopping cart is temporarily unavailable."},
                  "Redis failure exposes internal details")
            request("PUT", path, token=owner, body={"quantity": 1}, expected=503)
            request("DELETE", path, token=owner, expected=503)
            request("DELETE", "/cart", token=owner, expected=503)
        finally:
            compose("start", "redis")
            for _ in range(30):
                try:
                    get_cart()
                    break
                except AssertionError:
                    time.sleep(1)
            else:
                raise AssertionError("Cart did not recover after Redis restart")
    print("Cart HTTP checks passed: ownership, idempotent quantities, validation, "
          "stock safety, checkout, expiry refresh, expiry, cap, removal, and clear"
          + (", Redis outage/recovery" if args.outages else ""))


if __name__ == "__main__":
    main()
