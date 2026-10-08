"""Reusable fulfillment fixtures and asynchronous shipment polling."""
import json
import re
import time
from uuid import uuid4
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from support import BASE_URL, request, check, compose

def wait_for_shipment(order_id: str, token: str, timeout_seconds: float = 30) -> dict:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            with urlopen(Request(BASE_URL + f"/orders/{order_id}/shipment",
                                 headers={"Authorization": "Bearer " + token}), timeout=15) as response:
                check(response.headers.get("Cache-Control") == "no-store", "Shipment can be cached")
                return json.load(response)
        except HTTPError as error:
            with error:
                if error.code != 404:
                    raise AssertionError(f"Shipment poll returned HTTP {error.code}") from error
        time.sleep(0.5)
    raise AssertionError("Paid shipment did not appear within timeout")


def create_fulfillment_fixture(owner, development):
    marker=uuid4().hex
    output=compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--create-product", "Fulfillment " + marker, "Verification", "Verification", "5000", "2")
    product=int(re.search(r"Saved product (\d+) at version",output).group(1))
    order=request("POST","/orders",token=owner,key=marker,body={"items":[{"productId":product,"quantity":1}]},expected=201)
    payment=request("POST",f"/orders/{order['id']}/payments",token=owner,key=marker,expected=201)
    if development:
        request("POST",f"/dev/payments/{payment['id']}/simulate",token=owner,body={"outcome":"success"})
        shipment=wait_for_shipment(order['id'],owner)
    else:
        sid=uuid4()
        sql=f"SET XACT_ABORT ON; BEGIN TRAN; UPDATE dbo.Payments SET Status=N'Succeeded' WHERE Id='{payment['id']}'; UPDATE dbo.Orders SET Status=N'Paid' WHERE Id='{order['id']}'; INSERT dbo.Shipments (Id,OrderId,Status,CreatedAt) VALUES ('{sid}','{order['id']}',N'Pending',SYSUTCDATETIME()); COMMIT;"
        compose("exec","-T","sqlserver","bash","-lc",'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d Ecommerce -b -Q "'+sql+'"')
        shipment=request("GET",f"/orders/{order['id']}/shipment",token=owner)
    return order,payment,shipment,product
