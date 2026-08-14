#!/usr/bin/env bash
# Pull origin and publish the API if HEAD moved (or the output is missing).
# Safe to run from systemd ExecStartPre: GitHub down + existing build => start anyway.
set -euo pipefail

SRC="${CSX_API_SRC:-/opt/csx/src/api}"
OUT="${CSX_API_OUT:-/var/www/csx/api}"
BRANCH="${CSX_BRANCH:-main}"
WEB_USER="${CSX_WEB_USER:-www-data}"
DLL="$OUT/Csx.Api.dll"

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-/var/cache/csx/dotnet}"
mkdir -p "$DOTNET_CLI_HOME" "$OUT"

if [[ ! -d "$SRC/.git" ]]; then
  echo "API checkout missing at $SRC" >&2
  exit 1
fi

cd "$SRC"

if ! git fetch --quiet origin; then
  if [[ -f "$DLL" ]]; then
    echo "GitHub unreachable; keeping existing API publish"
    exit 0
  fi
  echo "GitHub unreachable and $DLL is missing" >&2
  exit 1
fi

git checkout --quiet "$BRANCH"
local_sha="$(git rev-parse HEAD)"
remote_sha="$(git rev-parse "origin/$BRANCH")"

need_build=0
if [[ "$local_sha" != "$remote_sha" ]]; then
  git reset --hard --quiet "origin/$BRANCH"
  need_build=1
fi
if [[ ! -f "$DLL" ]]; then
  need_build=1
fi

if [[ "$need_build" -eq 0 ]]; then
  echo "API already at $local_sha"
  exit 0
fi

echo "Publishing API $(git rev-parse --short HEAD)"
dotnet publish csc-stock-api/csc-stock-api.csproj -c Release -o "$OUT" --nologo
chown -R "$WEB_USER:$WEB_USER" "$OUT"
echo "Published to $OUT"
