#!/usr/bin/env bash
# Pull/build API + frontend, then bounce Kestrel so the new DLL is loaded.
set -euo pipefail

FRONTEND_UPDATE="${CSX_FRONTEND_UPDATE:-/opt/csx/src/frontend/deploy/update.sh}"
API_UPDATE="${CSX_API_UPDATE:-/opt/csx/src/api/deploy/update.sh}"

"$FRONTEND_UPDATE"
"$API_UPDATE"
systemctl restart csx-api.service
systemctl reload nginx.service
echo "CSX deploy complete"
