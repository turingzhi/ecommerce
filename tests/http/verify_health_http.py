"""Check health endpoints; --outages temporarily stops each Compose dependency.

Outage checks restore stopped services in finally blocks. Run on the local test stack.
"""

from support import check, compose, request

import argparse
import time


SERVICES = {"sqlserver", "rabbitmq", "elasticsearch", "redis"}


def dependencies(expected=200):
    result = request("GET", "/health/dependencies", expected=expected)
    check(set(result) == {"status", "dependencies"}, "Unexpected health response fields")
    check(set(result["dependencies"]) == SERVICES, "Missing dependency health results")
    check(set(result["dependencies"].values()) <= {"healthy", "unhealthy"},
          "Health response contains nongeneric dependency details")
    return result


def wait_healthy():
    for _ in range(30):
        try:
            result = dependencies()
            check(result["status"] == "healthy"
                  and all(value == "healthy" for value in result["dependencies"].values()),
                  "Healthy response has an unhealthy dependency")
            return
        except AssertionError:
            time.sleep(1)
    raise AssertionError("Dependencies did not become healthy after recovery")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--outages", action="store_true")
    args = parser.parse_args()
    check(request("GET", "/health/live") == {"status": "healthy"}, "Liveness response differs")
    check(request("GET", "/health") == {"status": "healthy"}, "Legacy SQL health differs")
    wait_healthy()

    if args.outages:
        for service in sorted(SERVICES):
            # Restoration also runs if stop or an assertion fails.
            try:
                compose("stop", service)
                result = dependencies(expected=503)
                check(result["status"] == "unhealthy", "Aggregate health did not fail")
                check(result["dependencies"][service] == "unhealthy",
                      f"Unavailable {service} was reported healthy")
                check(all(value == "healthy" for name, value in result["dependencies"].items()
                          if name != service), "Unrelated dependency reported unhealthy")
                check(request("GET", "/health/live") == {"status": "healthy"},
                      "Liveness depends on external infrastructure")
                sql_status = 503 if service == "sqlserver" else 200
                legacy = request("GET", "/health", expected=sql_status)
                check(legacy == {"status": "unhealthy" if sql_status == 503 else "healthy"},
                      "Legacy health behavior changed")
                print(f"PASS: {service} outage identified; liveness and legacy health preserved")
            finally:
                compose("start", service)
                wait_healthy()
    print("Health HTTP checks passed" + (": all four outages and recovery" if args.outages else ""))


if __name__ == "__main__":
    main()
