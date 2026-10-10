#!/bin/sh
# Azure Run Command runs this as root with the published image as $1.
set -eu
umask 077

image=${1:?Pass the published GHCR image}
case "$image" in
    ghcr.io/turingzhi/ecommerce:sha-*) revision=${image##*:sha-} ;;
    *) echo 'Unexpected image repository or tag' >&2; exit 1 ;;
esac
case "$revision" in
    ''|*[!0-9a-f]*) echo 'Invalid image revision' >&2; exit 1 ;;
esac
[ "${#revision}" -eq 40 ] || { echo 'Expected a full commit SHA' >&2; exit 1; }

cd "${ECOMMERCE_DEPLOY_DIR:-/home/azureuser/ecommerce}"
exec 9>.deploy.lock
flock -n 9 || { echo 'Another deployment is running' >&2; exit 1; }
for file in .env compose.yaml compose.override.yaml Caddyfile; do
    [ -f "$file" ] || { echo "Missing deployment file: $file" >&2; exit 1; }
done
previous_image=$(sed -n 's/^ECOMMERCE_IMAGE=//p' .env | tail -n 1)
[ -n "$previous_image" ] || { echo 'Current image is missing from .env' >&2; exit 1; }

# Root uses the same Docker registry login as the initial sudo docker pull.
export HOME=/root
export ECOMMERCE_IMAGE="$image"
docker compose config --quiet
# A failed pull must leave the currently deployed configuration unchanged.
docker compose pull ecommerce

env_tmp=$(mktemp .env.deploy.XXXXXX)
trap 'rm -f "$env_tmp"' 0
sed '/^ECOMMERCE_IMAGE=/d' .env > "$env_tmp"
printf 'ECOMMERCE_IMAGE=%s\n' "$image" >> "$env_tmp"
chmod 600 "$env_tmp"
chown --reference=.env "$env_tmp"
printf '%s\n' "$previous_image" > .previous-image
mv "$env_tmp" .env

# Only replace the app. Keep Caddy, dependencies, and all named volumes running.
# No automatic rollback: app startup may already have applied SQL migrations.
docker compose up -d --no-deps --wait --wait-timeout 300 ecommerce
container=$(docker compose ps -q ecommerce)
[ -n "$container" ] || { echo 'App container is missing' >&2; exit 1; }
running_image=$(docker inspect --format '{{.Config.Image}}' "$container")
[ "$running_image" = "$image" ] || { echo 'Unexpected running app image' >&2; exit 1; }
curl --fail --silent --show-error --connect-timeout 5 --max-time 15 \
    http://127.0.0.1:5088/health/dependencies |
    python3 -c 'import json,sys; d=json.load(sys.stdin); expected={"sqlserver","rabbitmq","elasticsearch","redis"}; assert d.get("status")=="healthy" and all(d.get("dependencies",{}).get(k)=="healthy" for k in expected), "Dependencies are not healthy"'

# Azure CLI can return success even when the guest script fails. The workflow
# requires this final marker in the returned guest output before accepting it.
printf 'ECOMMERCE_DEPLOYED=%s\n' "$image"
