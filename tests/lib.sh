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

# ---- stub rm / mv that fail on request ---------------------------------------
# mk_fs_stubs writes $work/bin/rm and $work/bin/mv (the tests put $work/bin first
# in the PATH of the installer). They stand in for a filesystem such as
# ntfs-3g/FUSE, where deleting a dir whose files a process has open fails
# ("Directory not empty") but renaming it works.
#   $work/rm_fail  one glob pattern per line; an rm with an argument matching any
#                  of them fails, for example '*/plugins/Browse Modes_*'
#   $work/mv_fail  one glob pattern per line, matched against the destination
#   $work/rm.log   every rm call
mk_fs_stubs() {
  cat > "$work/bin/rm" <<EOF2
#!/bin/bash
W="$work"
EOF2
  cat >> "$work/bin/rm" <<'EOF2'
echo "$*" >> "$W/rm.log"
if [ -s "$W/rm_fail" ]; then
  for a in "$@"; do
    while IFS= read -r pat; do
      [ -n "$pat" ] || continue
      case "$a" in $pat) echo "rm: cannot remove '$a': Directory not empty" >&2; exit 1 ;; esac
    done < "$W/rm_fail"
  done
fi
exec /bin/rm "$@"
EOF2
  cat > "$work/bin/mv" <<EOF2
#!/bin/bash
W="$work"
EOF2
  cat >> "$work/bin/mv" <<'EOF2'
if [ -s "$W/mv_fail" ]; then
  dest="${@: -1}"
  while IFS= read -r pat; do
    [ -n "$pat" ] || continue
    case "$dest" in $pat) echo "mv: cannot move to '$dest'" >&2; exit 1 ;; esac
  done < "$W/mv_fail"
fi
exec /bin/mv "$@"
EOF2
  chmod +x "$work/bin/rm" "$work/bin/mv"
}
