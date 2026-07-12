#!/usr/bin/env bash
# Deploy the API to the VPS: pg_dump backup -> pull -> rebuild -> restart ->
# readiness smoke test. Run from anywhere with SSH access to the VPS
# configured (matches the ssh invocations used throughout this repo's own
# deploy history).
#
# PROJECT-AUDIT-2026-07-10 OPS-01: db.Database.Migrate() still runs
# unconditionally inside the api container on every start (Program.cs,
# unchanged) -- moving migrations to a fully separate job with its own
# rollback gate is disproportionate engineering for this project's current
# scale (one VPS, one api instance, no replica count in docker-compose.yml,
# so the "multiple instances racing on the same migration" risk the audit
# raises doesn't apply here). What this script actually closes: the backup
# and post-deploy verification steps stop being something to remember by
# hand every time and become one non-optional command. It aborts before
# touching the running deployment if the backup looks wrong, and it uses
# the readiness endpoint (REL-03) as the smoke test the audit asked for.
set -euo pipefail

HOST="${VASS_DEPLOY_HOST:-root@vass.it-consult.services}"
REMOTE_DIR="${VASS_DEPLOY_DIR:-/root/vass}"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_FILE="backups/pre-deploy-${TIMESTAMP}.sql"
READY_TIMEOUT_S=300
READY_POLL_INTERVAL_S=10

echo "==> Backing up database ($BACKUP_FILE)..."
ssh "$HOST" "cd '$REMOTE_DIR' && mkdir -p backups && docker compose exec -T db pg_dump -U app -d vass > '$BACKUP_FILE'"

BACKUP_SIZE="$(ssh "$HOST" "wc -c < '$REMOTE_DIR/$BACKUP_FILE'" | tr -d '[:space:]')"
if [ -z "$BACKUP_SIZE" ] || [ "$BACKUP_SIZE" -lt 1000 ]; then
    echo "!! Backup file is suspiciously small (${BACKUP_SIZE:-0} bytes) -- aborting before touching the running deployment." >&2
    echo "!! Check '$REMOTE_DIR/$BACKUP_FILE' on the VPS manually." >&2
    exit 1
fi
echo "    OK ($BACKUP_SIZE bytes)"

echo "==> Pulling latest main and rebuilding api..."
ssh "$HOST" "cd '$REMOTE_DIR' && git pull && docker compose build api"

echo "==> Restarting api container..."
ssh "$HOST" "cd '$REMOTE_DIR' && docker compose up -d api"

echo "==> Waiting for readiness (up to ${READY_TIMEOUT_S}s)..."
elapsed=0
while [ "$elapsed" -lt "$READY_TIMEOUT_S" ]; do
    status="$(ssh "$HOST" "cd '$REMOTE_DIR' && docker compose exec -T api curl -s -o /dev/null -w '%{http_code}' http://localhost:5000/api/health/ready" 2>/dev/null || echo "000")"
    if [ "$status" = "200" ]; then
        echo "    ready (HTTP 200) after ${elapsed}s"
        echo
        ssh "$HOST" "cd '$REMOTE_DIR' && docker compose exec -T api curl -s http://localhost:5000/api/health/ready"
        echo
        echo "==> Deploy complete."
        exit 0
    fi
    sleep "$READY_POLL_INTERVAL_S"
    elapsed=$((elapsed + READY_POLL_INTERVAL_S))
done

echo "!! api did not become ready within ${READY_TIMEOUT_S}s (last status: $status)." >&2
echo "!! Check logs: ssh $HOST \"cd $REMOTE_DIR && docker compose logs api --tail 100\"" >&2
echo "!! Backup is at $REMOTE_DIR/$BACKUP_FILE if a rollback is needed." >&2
exit 1
