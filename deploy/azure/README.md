# Azure VM learning deployment

This Compose configuration runs one API/storefront instance and the existing
SQL Server, RabbitMQ, Elasticsearch, and Redis services on an Ubuntu x64 VM.
It pulls an already published GHCR image; no source checkout or build is needed
on the VM. This is for a disposable learning/demo environment, not real customers.

## Copy the configuration

Create `~/ecommerce` on the VM. Copy `compose.yaml` from this directory there,
using SCP from the Mac. Keep the SSH private key on the Mac.

## First start

Run these commands in `~/ecommerce` on the VM. The generated passwords stay in
the VM's `.env`; do not paste that file into chat or commit it. Use the generation
block only for the first start; changing it later doesn't rotate credentials in
existing database or broker volumes.

```sh
cd ~/ecommerce
sudo tee /etc/sysctl.d/99-ecommerce-elasticsearch.conf <<'EOF'
vm.max_map_count=1048576
EOF
sudo sysctl -p /etc/sysctl.d/99-ecommerce-elasticsearch.conf

if [ ! -e .env ]; then
umask 077
cat > .env <<EOF
ECOMMERCE_IMAGE=ghcr.io/turingzhi/ecommerce:sha-3f9c8b7e75e2485a9678a3a4e63f4a05c491d66e
MSSQL_SA_PASSWORD=Aa!$(openssl rand -hex 24)
RABBITMQ_PASSWORD=$(openssl rand -hex 24)
DEFAULT_ADMIN_ENABLED=false
EOF
fi
sudo docker compose config --quiet
sudo docker compose pull
sudo docker compose up -d --wait --wait-timeout 600
sudo docker compose ps
curl --fail --show-error http://127.0.0.1:5088/health/dependencies
```

The host setting follows [Elastic's Docker requirements](https://www.elastic.co/docs/deploy-manage/deploy/self-managed/install-elasticsearch-docker-prod)
and is preserved across VM restarts.

If the private GHCR package rejects the pull, authenticate on the VM with a
GitHub personal access token (classic) with `read:packages` and access to this
package. Use `sudo docker login ghcr.io --username YOUR_GITHUB_USERNAME` and
paste the token at its password prompt. Do not place the token in the command
line or the Compose file. Retry `sudo docker compose pull` afterward.

If startup fails, collect `sudo docker compose ps -a` and
`sudo docker compose logs --tail=80 SERVICE`, replacing SERVICE with the failing
service name. Review logs for secrets before sharing. Do not delete volumes to
resolve a startup error.

## Open the site through SSH

The only published port is `127.0.0.1:5088` on the VM. Supporting services have
no host ports. Open a second terminal on the Mac and run:

```sh
ssh -N -o ExitOnForwardFailure=yes -L 15088:127.0.0.1:5088 -i ~/Documents/azure/vm-ecommerce-demo_key.pem azureuser@9.205.18.58
```

Keep that connection open and browse to `http://127.0.0.1:15088`. This permits
testing over SSH before configuring a public hostname, HTTPS proxy, and Azure
inbound rules for ports 80 and 443.

## Enable public HTTPS

Do this after the first local health check succeeds. The optional
`compose.override.yaml` is loaded automatically by `docker compose` when it is
beside `compose.yaml`. Copy it and `Caddyfile` from the Mac:

```sh
scp -i ~/Documents/azure/vm-ecommerce-demo_key.pem \
  /Users/kaizhi/Documents/dot-beginner/Ecommerce/deploy/azure/compose.override.yaml \
  /Users/kaizhi/Documents/dot-beginner/Ecommerce/deploy/azure/Caddyfile \
  azureuser@9.205.18.58:~/ecommerce/
```

Set the public IP's Azure DNS name label to `turingzhi-ecommerce-demo`. Confirm
the resulting hostname resolves to the VM's public IP. In the VM's network
security group, allow inbound TCP 80 and TCP 443 from Any source, with unused
priorities such as 310 and 320. Keep SSH restricted to your current public IP.
If an OS firewall is active, it must also permit these web ports.

On the VM, preserve the existing image and passwords and add the hostname:

```sh
cd ~/ecommerce
sed -i '/^PUBLIC_HOSTNAME=/d' .env
printf '%s\n' 'PUBLIC_HOSTNAME=turingzhi-ecommerce-demo.denmarkeast.cloudapp.azure.com' >> .env
sudo docker compose config --quiet
sudo docker compose pull caddy
sudo docker compose run --rm --no-deps caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
sudo docker compose up -d --wait --wait-timeout 600
sudo docker compose logs --tail=50 caddy
curl --fail --show-error https://turingzhi-ecommerce-demo.denmarkeast.cloudapp.azure.com/health/dependencies
```

Caddy obtains and renews a public certificate and redirects HTTP to HTTPS.
Initial issuance may take a short time after startup. If the HTTPS request
fails, inspect Caddy's logs and port reachability; do not bypass certificate
verification or delete the certificate volume. Its `/data` and `/config` are
persisted in named volumes. See [Caddy's automatic HTTPS requirements](https://caddyserver.com/docs/automatic-https).

The override enables ASP.NET Core's environment switch for forwarded headers
so the app receives the original HTTPS scheme and client IP for rate limiting.
This switch trusts forwarding headers without a proxy IP allowlist. For this
isolated demo, Caddy is the only public web entry point; the app's host port
remains on loopback. Caddy shares a separate `web` network with the app, while
dependencies remain on the default network. Treat all VM users and containers
as trusted. Before using this for real customers, replace the switch with an
explicit trusted-proxy configuration in application code. See
[Microsoft's forwarded-header guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0#forward-the-scheme-for-linux-and-non-iis-reverse-proxies).

Once the HTTPS health check succeeds, open
`https://turingzhi-ecommerce-demo.denmarkeast.cloudapp.azure.com/` and test
registration, sign-in, and a cart operation. The SSH tunnel can then be closed.

## Data, restarts, and updates

- Database, broker, search, and ASP.NET Data Protection keys have named volumes.
  The initialization service sets the app home volume permissions for the
  .NET image's non-root user (UID 1654). Redis carts/cache remain temporary.
- Long-running services use `restart: unless-stopped`. Startup ordering applies
  to Compose startup, not Docker daemon restart; the app may retry by restarting
  while dependencies initialize after a VM restart.
- The app applies SQL migrations at startup. Image rollback doesn't undo them.
- Keep one app instance: background workers aren't coordinated across replicas.
- This demo uses SQL Server Developer edition, internal unencrypted service
  connections, and no database backups. It has no high-availability guarantee.
- To deploy a new verified revision, change only `ECOMMERCE_IMAGE` in `.env`,
  then run `sudo docker compose pull ecommerce` and
  `sudo docker compose up -d --no-deps --wait --wait-timeout 300 ecommerce`.
  Verify `/health/dependencies` and the storefront afterward. Keep the prior
  image reference for rollback, subject to database schema compatibility.
- Stop without deleting data with `sudo docker compose down`. Avoid `down -v`
  unless intentionally deleting the lab's data. At the end of the 20-day lab,
  delete the Azure resource group to remove its VM, disks, and networking.

## GitHub Actions continuous deployment

The `Ecommerce` workflow now has an optional `deploy` job after `publish`.
The sequence is tests → publish a commit-tagged GHCR image → Azure OIDC login
→ VM Run Command → local dependency check → public HTTPS checks. Pull requests
do not deploy. Revisions superseded while CI was running are skipped; deployment
jobs are serialized without cancelling a running VM operation. Brief app
downtime is expected during replacement. This is not a zero-downtime release.

The job stays disabled until the repository Actions variable
`AZURE_CD_ENABLED` is exactly `true`. Keep that flag unset until the steps below
are complete. Removing it later pauses new deployment jobs, not the running app
or an already running deployment.

### 1. Sign in and check the VM agent

On the Mac (not in the SSH session), run:

```sh
az login
az account set --subscription 'Azure subscription 1'
az account show --query '{subscription:name,tenant:tenantId}' --output table
az vm run-command invoke \
  --resource-group ecommerce-monthly-budget \
  --name vm-ecommerce-demo \
  --command-id RunShellScript \
  --scripts 'printf "AZURE_RUN_COMMAND_OK\n"' \
  --query 'value[].message' --output tsv
```

Continue only if the output includes `AZURE_RUN_COMMAND_OK`. Run Command uses
the Azure VM agent and normally takes at least around 20 seconds. If the agent
is not ready, diagnose its status first; do not open SSH to GitHub runners.
See [Azure Run Command](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/run-command).

### 2. Create the GitHub environment

In `turingzhi/ecommerce` on GitHub, select Settings → Environments → New
environment. Name it exactly `azure-demo`. Under Deployment branches and tags,
choose Selected branches and tags and add only the repository's default branch
(for example `main`, if that is its actual name). This branch restriction is
required because the Azure federation trusts the environment name. For this
learning demo, required reviewers are optional; leaving them unset permits
automatic deployment after CI. See [GitHub environment restrictions](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments#deployment-branches-and-tags).

### 3. Create a deployment identity

On the Mac, with the correct subscription still selected:

```sh
cd /Users/kaizhi/Documents/dot-beginner/Ecommerce
sh deploy/azure/setup-identity.sh
```

This creates/reuses the user-assigned managed identity `id-ecommerce-github`,
trusts GitHub's `repo:turingzhi/ecommerce:environment:azure-demo` OIDC subject,
and grants Virtual Machine Contributor scoped only to `vm-ecommerce-demo`.
That role permits privileged commands and other management of this VM; it is
not limited to changing a container. The identity is used by GitHub and does
not need to be attached to the VM. No client secret or SSH key is generated.
Creating the identity and assigning the role requires suitable Azure account
permissions, typically Owner on the resource group/subscription. Allow time for
new role assignments/federated credentials to propagate before the first run.
See [Azure GitHub OIDC setup](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect).

The script prints three IDs. Add them as environment secrets inside GitHub's
`azure-demo` environment:

| Secret | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | Managed identity client ID printed by the script |
| `AZURE_TENANT_ID` | Tenant ID printed by the script |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID printed by the script |

### 4. Commit the deployment files and enable CD

Review and commit `.github/workflows/ecommerce.yml`, `deploy/azure/`, and
`tests/deploy/test_azure_deploy.py`. Do not add the VM's real `.env`, credentials,
or private key. Push through your usual default-branch process. There is no
need to copy `deploy.sh` to the VM: Run Command sends the version checked out
by the workflow. Keep the existing VM Compose files and Caddyfile in
`/home/azureuser/ecommerce`; this job updates only the app image, not those files.

In GitHub Settings → Secrets and variables → Actions → Variables, add a
repository variable `AZURE_CD_ENABLED` with value `true`. Re-run all jobs of
the latest successful default-branch workflow containing the deployment job,
or push the next ordinary change. Confirm the Azure deployment job is present
and passes; a GitHub re-run uses the original run's workflow revision. Old runs
from before the job was committed cannot enable it by being re-run.

### Deployment failures and recovery

The VM must be running, Docker and the VM agent must be available, and the
root Docker login must be able to pull the GHCR package. For a private package,
use the existing `sudo docker login ghcr.io` setup with read access. The
workflow's Azure login does not grant GHCR access on the VM.

`deploy.sh` validates the image repository and full SHA, locks concurrent
deployments, and pulls before replacing the image entry in `.env`. Existing
passwords are preserved, the new `.env` stays mode 600 with its original owner,
and the previous image is recorded in `.previous-image` on the VM. Caddy and
dependencies continue running. A final success marker is emitted only after
the running image and local dependency health are checked. The workflow
requires that marker because Azure API success alone does not prove the guest
script succeeded. It also checks the public HTTPS health and storefront.

Failures after startup can leave the new image selected or running. Inspect
the job output and VM app logs. No automatic rollback is attempted because
SQL migrations may already have changed the database. If the previous image is
compatible with the current schema, use the manual image-update procedure above
to restore the reference saved in `.previous-image`. Keep all named volumes.

Run the deployment-script tests on Linux with
`python3 -m unittest discover -s tests/deploy -v`. These exercise real file
writes and file locking, replacing only Docker and HTTP calls. They do not
prove Azure authentication or the VM agent works; the first live workflow run
is the integration check.
