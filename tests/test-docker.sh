#!/usr/bin/env bash
# Tests for the --docker paths of install-browse-modes.sh. Runs in a scratch
# directory with a stub "docker" on PATH that keeps a fake container filesystem
# on disk (inspect, exec, cp, restart, port, info), a fake Jellyfin HTTP server,
# a file:// release tree and a stub crontab. Touches nothing outside it.
#
# Usage: tests/test-docker.sh
set -uo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo/install-browse-modes.sh"
work="$(mktemp -d)"
server_pid=""
pass=0
fail=0

cleanup() {
  if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; fi
  rm -rf "${work:?}"
}
trap cleanup EXIT

# shellcheck source=tests/lib.sh
. "$repo/tests/lib.sh"

# ---- fake Jellyfin -----------------------------------------------------------
# The X-Ctr header says which container is asking (the docker stub adds it to
# in-container requests). It answers 503 while that container's web root holds a
# bundle marked 9.9.9, to simulate an update that breaks the server.
cat > "$work/fake_jf.py" <<'PY'
import http.server, os, sys
work = sys.argv[1]
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        ctr = self.headers.get("X-Ctr") or open(os.path.join(work, "default_ctr")).read().strip()
        marker = os.path.join(work, "ctr", ctr, "fs", "jellyfin", "jellyfin-web", ".browse-modes-version")
        if os.path.exists(marker) and open(marker).read().strip() == "9.9.9":
            self.send_response(503); self.end_headers(); return
        v = open(os.path.join(work, "jf_version")).read().strip()
        body = ('{"ServerName":"t","Version":"%s","Id":"x"}' % v).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", 0), H)
print(s.server_address[1], flush=True)
s.serve_forever()
PY
echo "12.3.0" > "$work/jf_version"
echo "jf" > "$work/default_ctr"
python3 "$work/fake_jf.py" "$work" > "$work/port" &
server_pid=$!
for _ in $(seq 1 50); do [ -s "$work/port" ] && break; sleep 0.1; done
port="$(cat "$work/port")"

# ---- stub docker -------------------------------------------------------------
# Container NAME lives in $work/ctr/NAME: inspect.json and fs/ (its filesystem).
# Container paths map to fs/<path>. Switches (files in $work): docker_denied,
# curl_missing, wget_missing, cp_fail (one shot: the next copy INTO a container
# fails).
mkdir -p "$work/bin"
cat > "$work/bin/docker" <<EOF
#!/bin/bash
W="$work"
EOF
cat >> "$work/bin/docker" <<'EOF'
echo "$*" >> "$W/docker.log"
cmd="$1"; shift
ctr_of() { local n="${1%%:*}"; printf '%s' "$n"; }
is_ctr_path() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*:/ ]]; }
case "$cmd" in
  info)
    if [ -e "$W/docker_denied" ]; then echo "permission denied" >&2; exit 1; fi
    exit 0 ;;
  inspect)
    [ "$1" = "--type" ] && shift 2
    [ -f "$W/ctr/$1/inspect.json" ] || { echo "Error: No such container: $1" >&2; exit 1; }
    cat "$W/ctr/$1/inspect.json" ;;
  port)
    echo "127.0.0.1:$(cat "$W/port")" ;;
  restart)
    exit 0 ;;
  exec)
    while [ "${1:0:1}" = "-" ]; do
      case "$1" in -u) shift 2 ;; *) shift ;; esac
    done
    name="$1"; shift
    grep -q '"Running": true' "$W/ctr/$name/inspect.json" || { echo "Error: container $name is not running" >&2; exit 1; }
    fs="$W/ctr/$name/fs"
    case "$1" in
      curl|wget)
        if [ -e "$W/${1}_missing" ]; then echo "exec: $1: not found" >&2; exit 127; fi
        url=""; for a in "$@"; do case "$a" in http://*) url="$a" ;; esac; done
        path="/${url#http://127.0.0.1:8096/}"
        exec curl -fsS -H "X-Ctr: $name" "http://127.0.0.1:$(cat "$W/port")$path" ;;
    esac
    args=()
    for a in "$@"; do
      case "$a" in /*) args+=("$fs$a") ;; *) args+=("$a") ;; esac
    done
    exec "${args[@]}" ;;
  cp)
    src="$1"; dst="$2"
    if is_ctr_path "$src"; then
      n="$(ctr_of "$src")"; src="$W/ctr/$n/fs${src#*:}"
    fi
    if is_ctr_path "$dst"; then
      if [ -e "$W/cp_fail" ]; then rm -f "$W/cp_fail"; echo "cp: injected failure" >&2; exit 1; fi
      n="$(ctr_of "$dst")"; dst="$W/ctr/$n/fs${dst#*:}"
    fi
    case "$src" in
      */.) [ -d "$dst" ] || mkdir -p "$dst"; exec cp -a "$src" "$dst" ;;
      *) exec cp -a "$src" "$dst" ;;
    esac ;;
  *) echo "stub docker: unsupported: $cmd $*" >&2; exit 1 ;;
esac
EOF
cat > "$work/bin/crontab" <<EOF
#!/bin/sh
f="$work/crontab.txt"
case "\$1" in
  -l) if [ -s "\$f" ]; then cat "\$f"; else echo "no crontab" >&2; exit 1; fi ;;
  -) cat > "\$f" ;;
esac
EOF
chmod +x "$work/bin/docker" "$work/bin/crontab"

# mk_inspect <name> <bind|volume> <running 0|1> <web_ro 0|1>
mk_inspect() {
  python3 -I -c '
import json, sys
w, name, kind, running, web_ro = sys.argv[1:6]
mounts = []
if kind == "bind":
    mounts.append({"Type": "bind", "Source": w + "/vol/" + name + "-config", "Destination": "/config", "RW": True})
else:
    mounts.append({"Type": "volume", "Source": w + "/volumes/" + name + "/_data", "Destination": "/config", "RW": True})
if web_ro == "1":
    mounts.append({"Type": "bind", "Source": "/srv/web", "Destination": "/jellyfin/jellyfin-web", "RW": False})
doc = [{"Name": "/" + name, "State": {"Running": running == "1"},
        "Config": {"Env": ["JELLYFIN_WEB_DIR=/jellyfin/jellyfin-web", "JELLYFIN_DATA_DIR=/config"]},
        "HostConfig": {"NetworkMode": "bridge", "ReadonlyRootfs": False}, "Mounts": mounts}]
json.dump(doc, open(w + "/ctr/" + name + "/inspect.json", "w"), indent=1)
' "$work" "$@"
}

# new_ctr <name> [bind|volume] [web_ro]: a fresh stock Jellyfin container with an
# old plugin installed.
new_ctr() {
  local n="$1" kind="${2:-bind}" web_ro="${3:-0}" cfg
  rm -rf "${work:?}/ctr/$n" "${work:?}/vol/$n-config" "${work:?}/volumes/$n"
  mkdir -p "$work/ctr/$n/fs/jellyfin/jellyfin-web"
  echo STOCK > "$work/ctr/$n/fs/jellyfin/jellyfin-web/index.html"
  if [ "$kind" = "bind" ]; then
    cfg="$work/vol/$n-config"; mkdir -p "$cfg"; ln -s "$cfg" "$work/ctr/$n/fs/config"
  else
    cfg="$work/ctr/$n/fs/config"; mkdir -p "$cfg"
  fi
  mkdir -p "$cfg/plugins/Browse Modes_2.2.0.0"
  echo '{"name":"Browse Modes","version":"2.2.0.0"}' > "$cfg/plugins/Browse Modes_2.2.0.0/meta.json"
  mk_inspect "$n" "$kind" 1 "$web_ro"
  echo "$n" > "$work/default_ctr"
}
cfgdir() { # cfgdir <name>: where the container's /config really lives
  if [ -d "$work/vol/$1-config" ]; then printf '%s' "$work/vol/$1-config"; else printf '%s' "$work/ctr/$1/fs/config"; fi
}
webdir() { printf '%s' "$work/ctr/$1/fs/jellyfin/jellyfin-web"; }
restarts() { grep -c '^restart' "$work/docker.log" 2>/dev/null || true; }
reset_state() { rm -rf "${work:?}/state" "${work:?}/lib"; rm -f "$work/docker.log"; : > "$work/docker.log"; rm -f "$work"/{docker_denied,curl_missing,wget_missing,cp_fail}; }

# run_dk <args...>: runs the script against the scratch tree, output in $work/out.
run_dk() {
  PATH="$work/bin:$PATH" \
  BM_RAW_BASE="file://$work/rel/raw" BM_PLUGIN_REL_BASE="file://$work/rel/plugin" \
  BM_WEB_REL_BASE="file://$work/rel/web" BM_ALLOW_LOCAL_URLS=1 \
  BM_STATE_DIR="$work/state" BM_LIB_DIR="$work/lib" BM_SKIP_ROOT_CHECK=1 \
  BM_HEALTH_TRIES=3 BM_HEALTH_INTERVAL=0 \
    bash "$script" "$@" > "$work/out" 2>&1
}

state_file() { printf '%s' "$work/state/state.docker-$1"; }

# ---- 1. version gate ---------------------------------------------------------
echo "== version gate"
build_release 12.3.0 12.3.0 12.3
new_ctr jf; reset_state
echo "12.9.1" > "$work/jf_version"
run_dk --docker jf; rc=$?
check "unlisted version: interactive run exits 3" test "$rc" -eq 3
check "unlisted version: message says nothing changed" contains "$work/out" "nothing changed"
check "unlisted version: web untouched" contains "$(webdir jf)/index.html" "STOCK"
check "unlisted version: container never restarted" test "$(restarts)" -eq 0
run_dk --auto --docker jf; rc=$?
check "unlisted version: --auto exits 0" test "$rc" -eq 0
check "unlisted version: --auto is quiet on stdout" test ! -s "$work/out"
check "unlisted version: --auto logs the no-op with the container tag" contains "$work/state/updater.log" "[docker:jf] No tested Browse Modes release for Jellyfin 12.9"
check "unlisted version: state records no-entry" contains "$(state_file jf)" "last_result=no-entry"
echo "12.3.0" > "$work/jf_version"
sed -i 's/"12.3"/"12.2"/' "$work/rel/raw/compat.json"
run_dk --docker jf; rc=$?
check "entry keyed 12.2 is ignored on a 12.3 server" test "$rc" -eq 3

# ---- 2. refusals before touching anything ------------------------------------
echo "== verification"
build_release 12.3.0 12.3.0 12.3
sed -i 's/"webSha256":"[0-9a-f]*"/"webSha256":"PENDING"/' "$work/rel/raw/compat.json"
new_ctr jf; reset_state
run_dk --docker jf; rc=$?
check "PENDING web sha256 is refused" test "$rc" -ne 0
check "PENDING web sha256: web untouched, no restart" bash -c "grep -q STOCK '$(webdir jf)/index.html' && [ '$(restarts)' = 0 ]"
build_release 12.3.0 12.3.0 12.3
sed -i 's/"checksum":"[0-9a-f]*"/"checksum":"PENDING-CI"/' "$work/rel/raw/manifest.json"
run_dk --docker jf; rc=$?
check "PENDING plugin md5 is refused" test "$rc" -ne 0
check "PENDING plugin md5: nothing restarted" test "$(restarts)" -eq 0
build_release 12.3.0 12.3.0 12.3
sed -i 's/"checksum":"[0-9a-f]*"/"checksum":"00000000000000000000000000000000"/' "$work/rel/raw/manifest.json"
run_dk --docker jf; rc=$?
check "wrong plugin md5 is refused" test "$rc" -ne 0
check "wrong plugin md5 reports the mismatch" contains "$work/out" "Checksum mismatch"
build_release 12.3.0 12.3.0 12.3
sed -i 's/"webSha256":"[0-9a-f]*"/"webSha256":"0000000000000000000000000000000000000000000000000000000000000000"/' "$work/rel/raw/compat.json"
run_dk --docker jf; rc=$?
check "wrong web sha256 is refused" test "$rc" -ne 0
check "wrong web sha256: web untouched" contains "$(webdir jf)/index.html" "STOCK"

# ---- 3. install, backup, idempotency, recreate -------------------------------
echo "== install / idempotency / recreate"
build_release 12.3.0 12.3.0 12.3
new_ctr jf; reset_state
run_dk --dry-run --docker jf; rc=$?
check "dry run succeeds" test "$rc" -eq 0
check "dry run names the container paths" contains "$work/out" "container jf:/jellyfin/jellyfin-web"
check "dry run changes nothing" contains "$(webdir jf)/index.html" "STOCK"
check "dry run restarts nothing" test "$(restarts)" -eq 0
check "dry run writes no state" test ! -e "$work/state"
run_dk --docker jf; rc=$?
check "install succeeds" test "$rc" -eq 0
check "plugin installed into Browse Modes_12.3.0" test -f "$(cfgdir jf)/plugins/Browse Modes_12.3.0/meta.json"
check "plugin dll copied" test -f "$(cfgdir jf)/plugins/Browse Modes_12.3.0/a.dll"
check "old plugin dir removed" test ! -e "$(cfgdir jf)/plugins/Browse Modes_2.2.0.0"
check "web bundle installed" contains "$(webdir jf)/index.html" "12.3.0"
check "web marker written inside the container web root" contains "$(webdir jf)/.browse-modes-version" "12.3.0"
check "stock web file is gone (root was replaced, not merged)" test "$(grep -c STOCK "$(webdir jf)/index.html")" -eq 0
check "container restarted once" test "$(restarts)" -eq 1
check "bind-mounted config is written from the host (no docker cp for the plugin)" bash -c "! grep -q '^cp .*plugins' '$work/docker.log'"
check "backup holds the previous web root" bash -c "grep -q STOCK '$work'/state/backups/docker-jf/*/web/index.html"
check "backup holds the previous plugin" test -f "$work"/state/backups/docker-jf/*/plugin/"Browse Modes_2.2.0.0"/meta.json
check "state records the install" contains "$(state_file jf)" "last_result=ok"
check "state records the versions" bash -c "grep -q '^plugin=12.3.0' '$(state_file jf)' && grep -q '^web=12.3.0' '$(state_file jf)'"
check "log lines carry the container tag" contains "$work/state/updater.log" "[docker:jf] "
run_dk --auto --docker jf; rc=$?
check "second run exits 0" test "$rc" -eq 0
check "second run is a no-op (no restart)" test "$(restarts)" -eq 1
check "second run state is noop" contains "$(state_file jf)" "last_result=noop"

# The container was recreated from the image: web root is stock again, the config
# volume (and the plugin in it) survived.
rm -rf "$(webdir jf)"; mkdir -p "$(webdir jf)"; echo STOCK > "$(webdir jf)/index.html"
run_dk --auto --docker jf; rc=$?
check "recreated container: --auto exits 0" test "$rc" -eq 0
check "recreated container: web bundle is re-applied" contains "$(webdir jf)/index.html" "12.3.0"
check "recreated container: marker is back" contains "$(webdir jf)/.browse-modes-version" "12.3.0"
check "recreated container: plugin left alone" contains "$work/state/state.docker-jf" "plugin=12.3.0"
check "recreated container: restarted again" test "$(restarts)" -eq 2
check "recreated container: log says web only" contains "$work/state/state.docker-jf" "Installed web 12.3.0"

# Never downgrade.
echo '{"name":"Browse Modes","version":"12.3.5"}' > "$(cfgdir jf)/plugins/Browse Modes_12.3.0/meta.json"
echo 12.3.5 > "$(webdir jf)/.browse-modes-version"
run_dk --auto --docker jf; rc=$?
check "newer installed plugin/web are not downgraded" test "$(restarts)" -eq 2
check "downgrade run exits 0" test "$rc" -eq 0

# --plugin-only / --web-only
new_ctr jf; reset_state
run_dk --plugin-only --docker jf
check "--plugin-only installs the plugin" test -f "$(cfgdir jf)/plugins/Browse Modes_12.3.0/meta.json"
check "--plugin-only leaves the web root alone" contains "$(webdir jf)/index.html" "STOCK"

# ---- 4. named volume: docker cp route ---------------------------------------
echo "== named volume"
new_ctr vol volume; reset_state
run_dk --docker vol; rc=$?
check "named volume install succeeds" test "$rc" -eq 0
check "named volume: plugin written with docker cp" grep -q '^cp .*:/config/plugins/Browse Modes_12.3.0' "$work/docker.log"
check "named volume: plugin dir present with meta.json" test -f "$(cfgdir vol)/plugins/Browse Modes_12.3.0/meta.json"
check "named volume: old plugin dir removed" test ! -e "$(cfgdir vol)/plugins/Browse Modes_2.2.0.0"
check "named volume: backup holds the previous plugin" test -f "$work"/state/backups/docker-vol/*/plugin/"Browse Modes_2.2.0.0"/meta.json
check "named volume: web installed" contains "$(webdir vol)/.browse-modes-version" "12.3.0"

# ---- 5. rollback -------------------------------------------------------------
echo "== rollback"
build_release 12.3.0 9.9.9 12.3
new_ctr jf; reset_state
run_dk --docker jf; rc=$?
check "failed health check exits nonzero" test "$rc" -ne 0
check "failed health check restores the web root" contains "$(webdir jf)/index.html" "STOCK"
check "failed health check removes the new marker" test ! -e "$(webdir jf)/.browse-modes-version"
check "failed health check restores the old plugin" test -f "$(cfgdir jf)/plugins/Browse Modes_2.2.0.0/meta.json"
check "failed health check removes the new plugin" test ! -e "$(cfgdir jf)/plugins/Browse Modes_12.3.0"
check "rollback is reported" contains "$work/out" "rolled back"
check "rollback restarts the container again" test "$(restarts)" -eq 2
check "rollback records the result" contains "$(state_file jf)" "last_result=rolled-back"
# Same on a named volume (rollback through docker cp).
new_ctr vol volume; reset_state
run_dk --docker vol; rc=$?
check "named volume: failed health check restores plugin and web" bash -c "[ -f '$(cfgdir vol)/plugins/Browse Modes_2.2.0.0/meta.json' ] && [ ! -e '$(cfgdir vol)/plugins/Browse Modes_12.3.0' ] && grep -q STOCK '$(webdir vol)/index.html'"

# A copy that fails halfway: restore from the backup, no restart.
build_release 12.3.0 12.3.0 12.3
new_ctr jf; reset_state
touch "$work/cp_fail"
run_dk --docker jf; rc=$?
check "failed copy exits nonzero" test "$rc" -ne 0
check "failed copy restores the web root" contains "$(webdir jf)/index.html" "STOCK"
check "failed copy restores the old plugin" test -f "$(cfgdir jf)/plugins/Browse Modes_2.2.0.0/meta.json"
check "failed copy does not restart" test "$(restarts)" -eq 0

# ---- 6. container problems ---------------------------------------------------
echo "== container problems"
build_release 12.3.0 12.3.0 12.3
new_ctr jf; reset_state
mk_inspect jf bind 0 0
run_dk --auto --docker jf; rc=$?
check "stopped container: --auto exits 0" test "$rc" -eq 0
check "stopped container: --auto logs why" contains "$work/state/updater.log" "container jf is not running"
check "stopped container: state is skipped" contains "$(state_file jf)" "last_result=skipped"
run_dk --docker jf; rc=$?
check "stopped container: interactive run exits nonzero" test "$rc" -eq 1
check "stopped container: interactive message" contains "$work/out" "is not running"
run_dk --auto --docker nosuch; rc=$?
check "missing container: --auto exits 0" test "$rc" -eq 0
check "missing container: --auto logs why" contains "$work/state/updater.log" "container nosuch not found"
run_dk --docker nosuch; rc=$?
check "missing container: interactive run exits nonzero" test "$rc" -eq 1

# Image that is not Jellyfin: no jellyfin-web.
new_ctr other; reset_state
rm -rf "$(webdir other)"
run_dk --docker other; rc=$?
check "non-Jellyfin container is refused" test "$rc" -ne 0
check "non-Jellyfin refusal explains" contains "$work/out" "does not look like a Jellyfin image"
run_dk --auto --docker other; rc=$?
check "non-Jellyfin container is refused under --auto too" test "$rc" -ne 0
check "non-Jellyfin container: nothing restarted" test "$(restarts)" -eq 0
check "non-Jellyfin container: plugin dir untouched" test -f "$(cfgdir other)/plugins/Browse Modes_2.2.0.0/meta.json"

# No permission for the docker daemon.
new_ctr jf; reset_state
touch "$work/docker_denied"
run_dk --auto --docker jf; rc=$?
check "docker permission problem is an error" test "$rc" -ne 0
check "docker permission problem is explained" contains "$work/out" "docker group"
rm -f "$work/docker_denied"

# Read-only web root (for example a user's own bind mount): plugin still installs.
new_ctr rowc bind 1; reset_state
run_dk --docker rowc; rc=$?
check "read-only web root: run succeeds" test "$rc" -eq 0
check "read-only web root: web untouched" contains "$(webdir rowc)/index.html" "STOCK"
check "read-only web root: says so" contains "$work/out" "read-only"
check "read-only web root: plugin still installed" test -f "$(cfgdir rowc)/plugins/Browse Modes_12.3.0/meta.json"
run_dk --auto --docker rowc; rc=$?
check "read-only web root: later run is a quiet no-op" test "$rc" -eq 0

# ---- 7. how the version is read ----------------------------------------------
echo "== reaching Jellyfin"
new_ctr jf; reset_state
touch "$work/curl_missing"
run_dk --dry-run --docker jf; rc=$?
check "no curl in the container: wget is used" test "$rc" -eq 0
touch "$work/wget_missing"
run_dk --dry-run --docker jf; rc=$?
check "no curl or wget in the container: the published port is used" test "$rc" -eq 0
check "published port lookup went through docker port" grep -q '^port jf' "$work/docker.log"
rm -f "$work/curl_missing" "$work/wget_missing"
reset_state
run_dk --dry-run --docker jf --port "$port"; rc=$?
check "explicit --port is queried from the host" test "$rc" -eq 0
check "explicit --port never runs curl or wget in the container" bash -c "! grep -qE '^exec .* (curl|wget) ' '$work/docker.log'"

# ---- 8. several containers ---------------------------------------------------
echo "== several containers"
new_ctr a; new_ctr b volume; reset_state
run_dk --docker a; ra=$?
run_dk --docker b; rb=$?
check "two containers: both installs succeed" test "$ra" -eq 0 -a "$rb" -eq 0
check "two containers: separate state files" bash -c "[ -f '$(state_file a)' ] && [ -f '$(state_file b)' ]"
check "two containers: separate backup dirs" bash -c "[ -d '$work/state/backups/docker-a' ] && [ -d '$work/state/backups/docker-b' ]"
check "two containers: each updated" bash -c "grep -q 12.3.0 '$(webdir a)/.browse-modes-version' && grep -q 12.3.0 '$(webdir b)/.browse-modes-version'"

# ---- 9. cron -----------------------------------------------------------------
echo "== cron"
new_ctr a; new_ctr b; mk_inspect b bind 0 0; reset_state
printf '%s\n' '# keep me' '0 1 * * * /usr/bin/other-job' > "$work/crontab.txt"
dockerdir="$work/bin"
run_dk --enable-auto-update --docker a; rc=$?
check "docker enable succeeds" test "$rc" -eq 0
check "docker enable adds one line for the container" test "$(grep -c '# browse-modes-auto-update docker:a$' "$work/crontab.txt")" -eq 1
check "docker line runs the local copy with --auto --docker" grep -qE "^[0-9]+ [2-5] \* \* \* PATH=$dockerdir:[^ ]+ $work/lib/updater.sh --auto --docker a # browse-modes-auto-update docker:a$" "$work/crontab.txt"
check "docker line puts docker's directory first in PATH" grep -qE "PATH=$dockerdir:/usr/local/sbin" "$work/crontab.txt"
check "docker enable keeps other crontab lines" bash -c "grep -q '# keep me' '$work/crontab.txt' && grep -q other-job '$work/crontab.txt'"
check "local copy installed" cmp -s "$script" "$work/lib/updater.sh"
cp "$work/crontab.txt" "$work/crontab.before"
run_dk --enable-auto-update --docker a
check "docker enable twice: crontab byte-identical" cmp -s "$work/crontab.txt" "$work/crontab.before"
check "docker enable twice says already enabled" contains "$work/out" "already enabled"
run_dk --enable-auto-update --docker b; rc=$?
check "enable for a stopped container works with a note" test "$rc" -eq 0
check "enable for a stopped container notes it" contains "$work/out" "is not running"
run_dk --enable-auto-update; rc=$?
check "native enable coexists" test "$rc" -eq 0
check "now three marked lines" test "$(grep -c 'browse-modes-auto-update' "$work/crontab.txt")" -eq 3
check "native line ends in the plain marker" grep -qE "^[0-9]+ [2-5] \* \* \* $work/lib/updater.sh --auto # browse-modes-auto-update$" "$work/crontab.txt"
run_dk --enable-auto-update --docker nosuch; rc=$?
check "enable refuses an unknown container" test "$rc" -ne 0
check "refused enable added no line" test "$(grep -c 'browse-modes-auto-update' "$work/crontab.txt")" -eq 3
new_ctr other; rm -rf "$(webdir other)"
run_dk --enable-auto-update --docker other; rc=$?
check "enable refuses a non-Jellyfin container" test "$rc" -ne 0
touch "$work/docker_denied"
run_dk --enable-auto-update --docker a; rc=$?
check "enable refuses without docker access" test "$rc" -ne 0
rm -f "$work/docker_denied"
run_dk --enable-auto-update --docker 'a b'; rc=$?
check "enable refuses a container name with a space" test "$rc" -eq 2

run_dk --status --docker a; rc=$?
check "docker status reports enabled" contains "$work/out" "Auto-update:          enabled"
check "docker status names the container" contains "$work/out" "Docker container a"
check "docker status lists the other jobs" contains "$work/out" "Other browse-modes jobs"
run_dk --status; rc=$?
check "native status shows only the native job as its own" bash -c "grep -A1 'Auto-update:          enabled' '$work/out' | grep -q 'updater.sh --auto # browse-modes-auto-update$'"

run_dk --disable-auto-update --docker a; rc=$?
check "docker disable succeeds" test "$rc" -eq 0
check "docker disable removed only that container's line" bash -c "! grep -q 'docker:a\$' '$work/crontab.txt' && grep -q 'docker:b\$' '$work/crontab.txt' && grep -q -- '--auto # browse-modes-auto-update\$' '$work/crontab.txt'"
check "docker disable keeps the other job" contains "$work/crontab.txt" "/usr/bin/other-job"
check "docker disable keeps the local copy while jobs remain" test -e "$work/lib/updater.sh"
run_dk --disable-auto-update; rc=$?
check "native disable removes only the native line" bash -c "grep -q 'docker:b\$' '$work/crontab.txt' && ! grep -q -- '--auto # browse-modes-auto-update\$' '$work/crontab.txt'"
check "native disable keeps the local copy for the remaining docker job" test -e "$work/lib/updater.sh"
run_dk --enable-auto-update --docker a
run_dk --enable-auto-update
run_dk --disable-auto-update --all; rc=$?
check "--all succeeds" test "$rc" -eq 0
check "--all removes every browse-modes line" lacks "$work/crontab.txt" "browse-modes-auto-update"
check "--all keeps unrelated lines" contains "$work/crontab.txt" "/usr/bin/other-job"
check "--all removes the local copy" test ! -e "$work/lib/updater.sh"
run_dk --disable-auto-update --all --docker a; rc=$?
check "--all with --docker is refused" test "$rc" -eq 2
run_dk --all; rc=$?
check "--all without --disable-auto-update is refused" test "$rc" -eq 2
run_dk --disable-auto-update --docker a; rc=$?
check "disable when nothing is enabled is harmless" test "$rc" -eq 0
# Prefix-named containers must not be confused.
run_dk --enable-auto-update --docker a
new_ctr ab; run_dk --enable-auto-update --docker ab
run_dk --disable-auto-update --docker a
check "container 'a' does not match container 'ab'" bash -c "grep -q 'docker:ab\$' '$work/crontab.txt' && ! grep -q 'docker:a\$' '$work/crontab.txt'"

echo
echo "passed: $pass  failed: $fail"
[ "$fail" -eq 0 ]
