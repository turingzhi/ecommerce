"""Verify the configured default admin against the running API, optionally restarting it."""
import argparse
import os
import subprocess
import time
from uuid import uuid4
from support import assert_no_store, check, compose, login, register_and_login, request, scalar

EXPECTED = ["orders:read", "payments:read", "products:manage"]


def account_snapshot(email):
    literal = email.replace("'", "''")
    sql = f"SET NOCOUNT ON; SELECT Id + '|' + PasswordHash FROM dbo.AspNetUsers WHERE NormalizedEmail=UPPER(N'{literal}')"
    # Password/hash values remain in pipes and memory; never print them.
    return compose("exec", "-T", "sqlserver", "sh", "-c",
        'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd '
        '-S localhost -U sa -C -d Ecommerce -b -h -1 -W -Q "$1"', "admin-snapshot", sql).strip()


def check_expected_claims(user_id):
    literal = user_id.replace("'", "''")
    for permission in EXPECTED:
        check(scalar(f"SELECT COUNT(*) FROM dbo.AspNetUserClaims WHERE UserId=N'{literal}' AND ClaimType COLLATE Latin1_General_100_BIN2=N'permission' AND ClaimValue COLLATE Latin1_General_100_BIN2=N'{permission}'") == 1,
              "Expected default-admin permission missing or duplicated")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--restart", action="store_true", help="Restart the API and verify account/password preservation")
    args = parser.parse_args()
    enabled = compose("exec", "-T", "ecommerce", "printenv", "DefaultAdmin__Enabled").strip()
    check(enabled.lower() == "true", "Default admin setup is disabled")
    email = compose("exec", "-T", "ecommerce", "printenv", "DefaultAdmin__Email").strip()
    password = os.environ.get("ECOMMERCE_TEST_ADMIN_PASSWORD") or compose("exec", "-T", "ecommerce", "printenv", "DefaultAdmin__Password").rstrip("\r\n")
    check(bool(email and password), "A configured email and an initial/test password are required for login verification")
    token = login(email, password)
    me = request("GET", "/auth/me", token=token)
    check(set(EXPECTED).issubset(me['permissions']), "Default admin is missing a current admin permission")
    for path in ["/admin/orders", "/admin/payments", "/admin/products"]:
        request("GET", path, token=token)
        assert_no_store(path, token)
    literal = email.replace("'", "''")
    check(scalar(f"SELECT COUNT(*) FROM dbo.AspNetUsers WHERE NormalizedEmail=UPPER(N'{literal}')") == 1, "Default admin duplicated")
    check_expected_claims(me['userId'])
    customer = register_and_login(f"default-admin-customer-{uuid4().hex}@example.invalid", "DefaultAdminCustomer!123456")
    check(request("GET", "/auth/me", token=customer)['permissions'] == [], "Public registration inherited admin permissions")
    for path in ["/admin/orders", "/admin/payments", "/admin/products"]:
        request("GET", path, token=customer, expected=403)
    if args.restart:
        before = account_snapshot(email)
        compose("restart", "ecommerce")
        deadline = time.monotonic() + 45
        while True:
            try:
                request("GET", "/health")
                break
            except Exception:
                if time.monotonic() >= deadline: raise AssertionError("API did not recover after restart") from None
                time.sleep(0.5)
        renewed = login(email, password)
        check(request("GET", "/auth/me", token=renewed) == me, "Restart changed identity or permissions")
        check(account_snapshot(email) == before, "Restart changed the saved account or password hash")
        check_expected_claims(me['userId'])
    print("Default admin HTTP passed: login, all three permissions, independent customer registration" + (", repeated-startup account/password preservation" if args.restart else ""))

if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError:
        raise SystemExit("Compose command failed during default-admin verification; credential output suppressed.") from None
