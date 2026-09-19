#!/usr/bin/env bash
# Headless gateway (no control panel window).
#   ./start.sh              serve this computer only
#   ./start.sh --lan        serve every machine on the local network
set -euo pipefail
cd "$(dirname "$0")"
if [ ! -d .venv ]; then
  python3 -m venv .venv
  .venv/bin/pip install --upgrade pip -q
  .venv/bin/pip install -q -r requirements.txt
fi
exec .venv/bin/python -m launcher --config models.yaml "$@"
