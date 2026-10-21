#!/bin/sh
# Installs dependencies into the container-owned node_modules volume when package-lock.json changed, then serves with
# polling (bind mounts from Windows/macOS hosts do not deliver file-system events).
set -eu

STAMP=node_modules/.lock-hash
HASH=$(sha256sum package-lock.json | cut -d' ' -f1)
if [ ! -f "$STAMP" ] || [ "$(cat "$STAMP")" != "$HASH" ]; then
  echo "[web] package-lock.json changed or first run: npm ci"
  npm ci --ignore-scripts --no-audit --no-fund
  echo "$HASH" > "$STAMP"
fi

exec npx ng serve --host 0.0.0.0 --port 4200 --poll 1000 --configuration development
