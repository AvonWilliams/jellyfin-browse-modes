#!/usr/bin/env bash
# Shared helpers for the installer tests. Sourced; expects $work, $pass and $fail.

check() { # check <description> <command...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then pass=$((pass + 1)); echo "ok   - $desc"
  else fail=$((fail + 1)); echo "FAIL - $desc"; fi
}
contains() { grep -qF -- "$2" "$1"; }
lacks() { ! grep -qF -- "$2" "$1"; }

# ---- fake release tree -------------------------------------------------------
mkzip() {
  python3 -I -c '
import sys, zipfile
out = sys.argv[1]
with zipfile.ZipFile(out, "w") as z:
    for f in sys.argv[2:]:
        name, _, content = f.partition("=")
        z.writestr(name, content)
' "$@"
}

# build_release <plugin ver> <web ver> <jellyfin major.minor>
# Writes manifest.json, compat.json and the two zips under $work/rel.
build_release() {
  local pver="$1" wver="$2" key="$3" abi="$3.0" rel="$work/rel"
  rm -rf "${rel:?}"; mkdir -p "$rel/plugin/v$pver" "$rel/web/v$wver-web" "$rel/raw"
  mkzip "$rel/plugin/v$pver/browse-modes_${pver}_${abi}.zip" \
    "meta.json={\"name\":\"Browse Modes\",\"version\":\"$pver\",\"targetAbi\":\"$abi.0\"}" "a.dll=dll-$pver"
  mkzip "$rel/web/v$wver-web/jellyfin-web-browse-modes-$wver.zip" "index.html=<html>$wver</html>" "main.js=js"
  md5=$(md5sum "$rel/plugin/v$pver/browse-modes_${pver}_${abi}.zip" | cut -d' ' -f1)
  sha=$(sha256sum "$rel/web/v$wver-web/jellyfin-web-browse-modes-$wver.zip" | cut -d' ' -f1)
  cat > "$rel/raw/manifest.json" <<JSON
[{"versions":[{"version":"$pver","targetAbi":"$abi.0","sourceUrl":"file://$rel/plugin/v$pver/browse-modes_${pver}_${abi}.zip","checksum":"$md5"}]}]
JSON
  cat > "$rel/raw/compat.json" <<JSON
{"schema":1,"releases":{"$key":{"plugin":"$pver","web":"$wver","webSha256":"$sha"}}}
JSON
}
