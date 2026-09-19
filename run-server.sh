#!/usr/bin/env bash
# Headless gateway (no window). The gateway itself is cross-platform;
# only the WPF desktop shell is Windows-only.
#   ./run-server.sh            serve this computer only
#   ./run-server.sh --lan      serve every machine on the local network
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --project src/Padra.Web/Padra.Web.csproj -c Release -- --config models.yaml "$@"
