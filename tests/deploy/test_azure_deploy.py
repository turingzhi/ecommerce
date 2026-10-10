"""Exercise deployment state changes without a live Docker daemon or Azure VM.

Run on Linux: python3 -m unittest discover -s tests/deploy -v
Only Docker and HTTP are replaced; filesystem permissions and locking are real.
"""
import fcntl
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "deploy/azure/deploy.sh"
OLD = "ghcr.io/turingzhi/ecommerce:sha-1111111111111111111111111111111111111111"
NEW = "ghcr.io/turingzhi/ecommerce:sha-2222222222222222222222222222222222222222"
ENV_TEXT = f"ECOMMERCE_IMAGE={OLD}\nMSSQL_SA_PASSWORD=KeepThisPassword!\nPUBLIC_HOSTNAME=demo.example.com\n"


class AzureDeployTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "Azure deployment script is missing")
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.bin = self.directory / "bin"
        self.bin.mkdir()
        self.env_file = self.directory / ".env"
        self.env_file.write_text(ENV_TEXT)
        self.env_file.chmod(0o600)
        for name in ("compose.yaml", "compose.override.yaml", "Caddyfile"):
            (self.directory / name).touch()
        self.log = self.directory / "calls.jsonl"
        self.write_tool("docker", '''#!/usr/bin/env python3
import json, os, sys
with open(os.environ["TEST_CALLS"], "a") as log:
    log.write(json.dumps(sys.argv[1:]) + "\\n")
if "pull" in sys.argv and os.environ.get("FAIL_PULL"):
    sys.exit(1)
if "up" in sys.argv and os.environ.get("FAIL_UP"):
    sys.exit(1)
if "inspect" in sys.argv:
    print(os.environ["ECOMMERCE_IMAGE"])
if "ps" in sys.argv:
    print("test-container")
''')
        self.write_tool("curl", '''#!/usr/bin/env python3
import os
print('{"status":"unhealthy"}' if os.environ.get("FAIL_HEALTH") else
      '{"status":"healthy","dependencies":{"sqlserver":"healthy","rabbitmq":"healthy","elasticsearch":"healthy","redis":"healthy"}}')
''')

    def write_tool(self, name, content):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def run_deploy(self, image=NEW, **settings):
        env = dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                   ECOMMERCE_DEPLOY_DIR=str(self.directory), TEST_CALLS=str(self.log))
        env.update(settings)
        return subprocess.run(["sh", str(SCRIPT), image], env=env,
                              capture_output=True, text=True, timeout=10)

    def test_success_preserves_secrets_and_records_previous_revision(self):
        result = self.run_deploy()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.env_file.read_text(),
                         "MSSQL_SA_PASSWORD=KeepThisPassword!\nPUBLIC_HOSTNAME=demo.example.com\n"
                         f"ECOMMERCE_IMAGE={NEW}\n")
        self.assertEqual(self.env_file.stat().st_mode & 0o777, 0o600)
        self.assertEqual((self.directory / ".previous-image").read_text(), OLD + "\n")
        self.assertIn(f"ECOMMERCE_DEPLOYED={NEW}", result.stdout)
        self.assertNotIn("KeepThisPassword!", result.stdout + result.stderr)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()]
        self.assertIn(["compose", "up", "-d", "--no-deps", "--wait",
                       "--wait-timeout", "300", "ecommerce"], calls)

    def test_invalid_image_is_rejected_before_any_mutation(self):
        for image in ("ghcr.io/other/app:latest", NEW + ";id", "ghcr.io/turingzhi/ecommerce:sha-"):
            with self.subTest(image=image):
                result = self.run_deploy(image)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(self.env_file.read_text(), ENV_TEXT)
                self.assertFalse(self.log.exists())

    def test_failed_pull_leaves_current_configuration_untouched(self):
        result = self.run_deploy(FAIL_PULL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.env_file.read_text(), ENV_TEXT)
        self.assertFalse((self.directory / ".previous-image").exists())
        self.assertNotIn("ECOMMERCE_DEPLOYED=", result.stdout)

    def test_failed_start_does_not_claim_success_or_rollback_migrations(self):
        result = self.run_deploy(FAIL_UP="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(NEW, self.env_file.read_text())
        self.assertEqual((self.directory / ".previous-image").read_text(), OLD + "\n")
        self.assertNotIn("ECOMMERCE_DEPLOYED=", result.stdout)

    def test_unhealthy_dependencies_do_not_claim_success(self):
        result = self.run_deploy(FAIL_HEALTH="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("ECOMMERCE_DEPLOYED=", result.stdout)

    def test_concurrent_deployment_is_rejected_without_changes(self):
        with (self.directory / ".deploy.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            result = self.run_deploy()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.env_file.read_text(), ENV_TEXT)
        self.assertFalse(self.log.exists())


if __name__ == "__main__":
    unittest.main()
