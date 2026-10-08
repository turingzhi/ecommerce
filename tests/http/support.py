"""Shared HTTP, account and Compose helpers; importing this module has no side effects."""
import json
import os
import subprocess
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

BASE_URL = os.environ.get("ECOMMERCE_BASE_URL", "http://127.0.0.1:5088").rstrip("/")
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]

def request(method, path, *, body=None, token=None, key=None, expected=200):
    headers = {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if token is not None:
        headers["Authorization"] = f"Bearer {token}"
    if key is not None:
        headers["Idempotency-Key"] = key

    data = json.dumps(body).encode("utf-8") if body is not None else None
    http_request = Request(BASE_URL + path, data=data, headers=headers, method=method)
    try:
        with urlopen(http_request, timeout=15) as response:
            status, content = response.status, response.read()
    except HTTPError as error:
        status, content = error.code, error.read()

    if status != expected:
        raise AssertionError(f"{method} {path}: expected HTTP {expected}, got {status}")
    return json.loads(content) if content else None


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def register_and_login(email, password):
    credentials = {"email": email, "password": password}
    request("POST", "/auth/register", body=credentials)
    login = request("POST", "/auth/login?useCookies=false", body=credentials)
    token = login.get("accessToken")
    check(isinstance(token, str) and bool(token), "Login did not return an access token")
    return token


def compose(*args):
    return subprocess.check_output(
        ["docker", "compose", *args], text=True, stderr=subprocess.STDOUT, cwd=REPOSITORY_ROOT
    )


def scalar(query):
    # Password stays inside the SQL container and is not put in host arguments.
    output = compose(
        "exec", "-T", "sqlserver", "sh", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd '
        '-S localhost -U sa -C -d Ecommerce -b -h -1 -W -Q "$1"',
        "sql-check", "SET NOCOUNT ON; " + query,
    )
    return int(output.strip())


def login(email, password):
    return request("POST", "/auth/login?useCookies=false", body={"email": email, "password": password})["accessToken"]


def grant_admin_permission(email, permission):
    for _ in range(2):
        compose("exec", "-T", "ecommerce", "dotnet", "Ecommerce.Api.dll", "--grant-" + permission + "-admin", email)
    check(scalar(f"SELECT COUNT(*) FROM dbo.AspNetUserClaims c JOIN dbo.AspNetUsers u ON c.UserId=u.Id WHERE u.Email='{email}' AND c.ClaimType='permission' AND c.ClaimValue='{permission}s:manage'") == 1,
          "Repeated permission grant duplicated claim")


def assert_no_store(path, token):
    with urlopen(Request(BASE_URL+path,headers={"Authorization":"Bearer "+token}),timeout=15) as response:
        check(response.headers.get("Cache-Control")=="no-store","Response is cacheable")
