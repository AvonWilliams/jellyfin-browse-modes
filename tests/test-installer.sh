#!/usr/bin/env bash
# Tests for install-browse-modes.sh. Runs entirely in a scratch directory: a
# fake Jellyfin (tiny local HTTP server), a file:// release tree, a stub
# systemctl and a stub crontab. Touches nothing outside the scratch directory.
#
# Usage: tests/test-installer.sh
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
# Version comes from $work/jf_version. It answers 503 while the web root holds a
# bundle marked 9.9.9 (to simulate an update that breaks the server) and while
# $work/jf_down exists (the stub systemctl creates it on stop and on a failing
# start, and removes it on a successful start).
cat > "$work/fake_jf.py" <<'PY'
import http.server, os, sys
work = sys.argv[1]
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        marker = os.path.join(work, "web", ".browse-modes-version")
        if os.path.exists(os.path.join(work, "jf_down")):
            self.send_response(503); self.end_headers(); return
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
python3 "$work/fake_jf.py" "$work" > "$work/port" &
server_pid=$!
for _ in $(seq 1 50); do [ -s "$work/port" ] && break; sleep 0.1; done
port="$(cat "$work/port")"

# ---- stub systemctl / crontab ------------------------------------------------
mkdir -p "$work/bin"
# Switches: $work/start_fail holds how many "systemctl start" calls fail (a
# number, or "all"); a failing start leaves the server down. $work/stop_fail
# makes "systemctl stop" fail.
cat > "$work/bin/systemctl" <<EOF
#!/bin/sh
W="$work"
EOF
cat >> "$work/bin/systemctl" <<'STUB'
echo "$*" >> "$W/systemctl.log"
case "$1" in
  stop) touch "$W/jf_down"; [ -e "$W/stop_fail" ] && exit 1 ;;
  start)
    if [ -s "$W/start_fail" ]; then
      n=$(cat "$W/start_fail")
      if [ "$n" != all ]; then
        n=$((n - 1))
        if [ "$n" -le 0 ]; then rm -f "$W/start_fail"; else echo "$n" > "$W/start_fail"; fi
      fi
      touch "$W/jf_down"; echo "Job for jellyfin.service failed" >&2; exit 1
    fi
    rm -f "$W/jf_down" ;;
esac
exit 0
STUB
cat > "$work/bin/crontab" <<EOF
#!/bin/sh
# File-backed fake of the crontab command: -l prints, - replaces.
f="$work/crontab.txt"
case "\$1" in
  -l) if [ -s "\$f" ]; then cat "\$f"; else echo "no crontab for root" >&2; exit 1; fi ;;
  -) cat > "\$f" ;;
esac
EOF
chmod +x "$work/bin/systemctl" "$work/bin/crontab"

# run_installer <args...>: runs the script against the scratch dirs, output in $work/out.
run_installer() {
  PATH="$work/bin:$PATH" \
  BM_RAW_BASE="file://$work/rel/raw" BM_PLUGIN_REL_BASE="file://$work/rel/plugin" \
  BM_WEB_REL_BASE="file://$work/rel/web" BM_ALLOW_LOCAL_URLS=1 BM_SKIP_ROOT_CHECK=1 \
  BM_STATE_DIR="$work/state" BM_LIB_DIR="$work/lib" BM_PLUGIN_DIR="$work/plugins" BM_WEB_DIR="$work/web" \
  BM_HEALTH_TRIES=3 BM_HEALTH_INTERVAL=0 \
    bash "$script" --host 127.0.0.1 --port "$port" "$@" > "$work/out" 2>&1
}

reset_target() {
  rm -rf "${work:?}/plugins" "${work:?}/web" "${work:?}/state" "${work:?}/lib"
  rm -f "${work:?}/systemctl.log" "${work:?}/start_fail" "${work:?}/stop_fail" "${work:?}/jf_down"
  mkdir -p "$work/plugins" "$work/web"
}

# ---- 1. version gate ---------------------------------------------------------
echo "== version gate"
build_release 12.3.0 12.3.0 12.3
reset_target
echo "OLD" > "$work/web/index.html"
echo "12.9.1" > "$work/jf_version"
run_installer; rc=$?
check "unlisted Jellyfin version: interactive run exits 3" test "$rc" -eq 3
check "unlisted Jellyfin version: message says nothing changed" contains "$work/out" "nothing changed"
check "unlisted Jellyfin version: web dir untouched" contains "$work/web/index.html" "OLD"
check "unlisted Jellyfin version: server never stopped" test ! -e "$work/systemctl.log"
run_installer --auto; rc=$?
check "unlisted Jellyfin version: --auto exits 0" test "$rc" -eq 0
check "unlisted Jellyfin version: --auto is quiet on stdout" test ! -s "$work/out"
check "unlisted Jellyfin version: --auto logs the no-op" contains "$work/state/updater.log" "No tested Browse Modes release for Jellyfin 12.9"
check "unlisted Jellyfin version: state records no-entry" contains "$work/state/state" "last_result=no-entry"
run_installer --dry-run; rc=$?
check "unlisted Jellyfin version: --dry-run also refuses" test "$rc" -eq 3

echo "12.3.0" > "$work/jf_version"
# An entry for a different major.minor must not be used for this server.
sed -i 's/"12.3"/"12.2"/' "$work/rel/raw/compat.json"
run_installer; rc=$?
check "entry keyed 12.2 is ignored on a 12.3 server" test "$rc" -eq 3

# ---- 2. refusals before touching anything ------------------------------------
echo "== verification"
build_release 12.3.0 12.3.0 12.3
sed -i 's/"webSha256":"[0-9a-f]*"/"webSha256":"PENDING"/' "$work/rel/raw/compat.json"
reset_target; echo OLD > "$work/web/index.html"
run_installer; rc=$?
check "PENDING web sha256 is refused" test "$rc" -ne 0
check "PENDING web sha256: server never stopped" test ! -e "$work/systemctl.log"
check "PENDING web sha256: web dir untouched" contains "$work/web/index.html" "OLD"

build_release 12.3.0 12.3.0 12.3
sed -i 's/"checksum":"[0-9a-f]*"/"checksum":"PENDING-CI"/' "$work/rel/raw/manifest.json"
reset_target; echo OLD > "$work/web/index.html"
run_installer; rc=$?
check "PENDING plugin md5 is refused" test "$rc" -ne 0
check "PENDING plugin md5: nothing stopped or replaced" test ! -e "$work/systemctl.log"

build_release 12.3.0 12.3.0 12.3
sed -i 's/"checksum":"[0-9a-f]*"/"checksum":"00000000000000000000000000000000"/' "$work/rel/raw/manifest.json"
reset_target; echo OLD > "$work/web/index.html"
run_installer; rc=$?
check "wrong plugin md5 is refused" test "$rc" -ne 0
check "wrong plugin md5 reports the mismatch" contains "$work/out" "Checksum mismatch"
check "wrong plugin md5: nothing stopped" test ! -e "$work/systemctl.log"

build_release 12.3.0 12.3.0 12.3
sed -i 's/"webSha256":"[0-9a-f]*"/"webSha256":"0000000000000000000000000000000000000000000000000000000000000000"/' "$work/rel/raw/compat.json"
reset_target; echo OLD > "$work/web/index.html"
run_installer; rc=$?
check "wrong web sha256 is refused" test "$rc" -ne 0
check "wrong web sha256: nothing stopped" test ! -e "$work/systemctl.log"

# A 12.x-numbered web bundle that belongs to another Jellyfin version.
build_release 12.3.0 12.2.0 12.3
reset_target
run_installer; rc=$?
check "web bundle of another Jellyfin major.minor is refused" test "$rc" -ne 0
check "web bundle mismatch: server never stopped" test ! -e "$work/systemctl.log"

# ---- 3. install, idempotency, downgrade --------------------------------------
echo "== install / idempotency"
build_release 12.3.0 12.3.0 12.3
reset_target; echo OLD > "$work/web/index.html"
mkdir -p "$work/plugins/Browse Modes_2.2.0.0"
echo '{"name":"Browse Modes","version":"2.2.0.0"}' > "$work/plugins/Browse Modes_2.2.0.0/meta.json"
run_installer --dry-run; rc=$?
check "dry run succeeds" test "$rc" -eq 0
check "dry run changes nothing" contains "$work/web/index.html" "OLD"
check "dry run never stopped the server" test ! -e "$work/systemctl.log"
run_installer; rc=$?
check "install succeeds" test "$rc" -eq 0
check "plugin installed into Browse Modes_12.3.0" test -f "$work/plugins/Browse Modes_12.3.0/meta.json"
check "old plugin dir removed" test ! -e "$work/plugins/Browse Modes_2.2.0.0"
check "web bundle installed" contains "$work/web/index.html" "12.3.0"
check "web marker written" contains "$work/web/.browse-modes-version" "12.3.0"
check "server stopped then started" contains "$work/systemctl.log" "stop jellyfin"
check "backup holds the previous web root" bash -c "grep -q OLD '$work'/state/backups/*/web/index.html"
check "backup holds the previous plugin" test -f "$work"/state/backups/*/plugin/"Browse Modes_2.2.0.0"/meta.json
check "state records the install" contains "$work/state/state" "last_result=ok"
check "state records the plugin version" contains "$work/state/state" "plugin=12.3.0"
rm -f "$work/systemctl.log"
run_installer --auto; rc=$?
check "second run exits 0" test "$rc" -eq 0
check "second run is a no-op (server not stopped)" test ! -e "$work/systemctl.log"
check "second run state is noop" contains "$work/state/state" "last_result=noop"

# A Jellyfin upgrade replaced the web root: the marker is gone, so re-apply web only.
rm -rf "${work:?}/web"; mkdir -p "$work/web"; echo STOCK > "$work/web/index.html"
run_installer --auto; rc=$?
check "stock web root after a Jellyfin upgrade is repaired" contains "$work/web/index.html" "12.3.0"
check "repair left the plugin alone" test -f "$work/plugins/Browse Modes_12.3.0/meta.json"

# The marker alone is not enough: the files must still be ours.
echo "== web fingerprint"
check "install records a file fingerprint next to the marker" contains "$work/web/.browse-modes-files" "  index.html"
rm -f "$work/systemctl.log"
run_installer --auto; rc=$?
check "untouched bundle: no-op exit 0" test "$rc" -eq 0
check "untouched bundle: server not restarted" test ! -e "$work/systemctl.log"
check "untouched bundle: state is noop" contains "$work/state/state" "last_result=noop"
# apt/dpkg reinstall: stock files are back, marker and our extra chunk survived.
echo "<html>STOCK reinstall</html>" > "$work/web/index.html"
rm -f "$work/systemctl.log"
run_installer --auto; rc=$?
check "reinstalled stock files with the marker intact: exit 0" test "$rc" -eq 0
check "reinstalled stock files with the marker intact: bundle re-applied" contains "$work/web/index.html" "12.3.0"
check "reinstalled stock files: server stopped and started" bash -c "grep -q 'stop jellyfin' '$work/systemctl.log' && grep -q 'start jellyfin' '$work/systemctl.log'"
check "reinstalled stock files: reason is logged" contains "$work/state/updater.log" "no longer matches the installed bundle"
check "reinstalled stock files: backup holds the replaced files" bash -c "grep -q 'STOCK reinstall' '$work'/state/backups/*/web/index.html"
check "reinstalled stock files: state records the install" contains "$work/state/state" "last_result=ok"
rm -f "$work/systemctl.log"
run_installer --auto
check "after the re-apply the next run is a no-op again" test ! -e "$work/systemctl.log"
# Same-minor upgrade of jellyfin-web (12.3.0 -> 12.3.1): different index.html, marker intact.
echo "<html>stock 12.3.1</html>" > "$work/web/index.html"
echo "newer chunk" > "$work/web/main.js"
rm -f "$work/systemctl.log"
run_installer --auto; rc=$?
check "same-minor web upgrade: bundle re-applied" bash -c "grep -q '12.3.0' '$work/web/index.html' && ! grep -q 'stock 12.3.1' '$work/web/index.html'"
check "same-minor web upgrade: server restarted" contains "$work/systemctl.log" "start jellyfin"
check "same-minor web upgrade: exit 0" test "$rc" -eq 0
# A bundle file that disappeared counts as changed too.
rm -f "$work/web/index.html"
rm -f "$work/systemctl.log"
run_installer --auto
check "missing bundle file: re-applied" contains "$work/web/index.html" "12.3.0"
# Marker written by an older script (no fingerprint): installed once more to record one.
rm -f "$work/web/.browse-modes-files"
rm -f "$work/systemctl.log"
run_installer --auto; rc=$?
check "marker without fingerprint: re-applied once" contains "$work/systemctl.log" "start jellyfin"
check "marker without fingerprint: says why" contains "$work/state/updater.log" "no file fingerprint is recorded"
check "marker without fingerprint: fingerprint written" test -s "$work/web/.browse-modes-files"
rm -f "$work/systemctl.log"
run_installer --auto
check "marker without fingerprint: second run is a no-op" test ! -e "$work/systemctl.log"
# --status says so, and --dry-run reports the work.
echo "<html>STOCK again</html>" > "$work/web/index.html"
run_installer --status
check "status flags files that no longer match" contains "$work/out" "no longer matches the installed bundle"
run_installer --dry-run
check "dry run plans the re-apply" contains "$work/out" "web bundle 12.3.0 (sha256 ok)"
check "dry run did not touch the files" contains "$work/web/index.html" "STOCK again"
run_installer --auto

# Never downgrade.
echo '{"name":"Browse Modes","version":"12.3.5"}' > "$work/plugins/Browse Modes_12.3.0/meta.json"
echo 12.3.5 > "$work/web/.browse-modes-version"
rm -f "$work/systemctl.log"
run_installer --auto; rc=$?
check "newer installed plugin/web are not downgraded" test ! -e "$work/systemctl.log"
check "downgrade run exits 0" test "$rc" -eq 0

# ---- 4. rollback -------------------------------------------------------------
echo "== rollback"
# A web bundle numbered 9.9.9 makes the fake server answer 503 once installed.
build_release 12.3.0 9.9.9 12.3
reset_target; echo OLD > "$work/web/index.html"
run_installer; rc=$?
check "failed health check exits nonzero" test "$rc" -ne 0
check "failed health check restores the web root" contains "$work/web/index.html" "OLD"
check "rollback is reported" contains "$work/out" "rolled back"
check "rollback leaves no new marker" test ! -e "$work/web/.browse-modes-version"
check "rollback records the result" contains "$work/state/state" "last_result=rolled-back"
check "rollback restarts the server last" bash -c "tail -1 '$work/systemctl.log' | grep -q 'start jellyfin'"

# A failing "systemctl start" (ExecStartPre failure, start-limit-hit) must not
# end the run before the health check and the rollback.
echo "== failing start"
build_release 12.3.0 12.3.0 12.3
reset_target; echo OLD > "$work/web/index.html"
echo 1 > "$work/start_fail"
run_installer; rc=$?
check "failing first start: exit 1" test "$rc" -eq 1
check "failing first start: files rolled back" contains "$work/web/index.html" "OLD"
check "failing first start: no new marker" test ! -e "$work/web/.browse-modes-version"
check "failing first start: state records rolled-back" contains "$work/state/state" "last_result=rolled-back"
check "failing first start: reported as rolled back" contains "$work/out" "rolled back"
check "failing first start: server started again by the rollback" bash -c "tail -1 '$work/systemctl.log' | grep -q 'start jellyfin'"
check "failing first start: server answers after the rollback" test ! -e "$work/jf_down"
check "failing first start: start failure is logged" contains "$work/state/updater.log" "systemctl start jellyfin failed"
check "failing first start: rollback asks systemd to reset the failed state" contains "$work/systemctl.log" "reset-failed jellyfin"
reset_target; echo OLD > "$work/web/index.html"
echo all > "$work/start_fail"
run_installer --auto; rc=$?
check "start never works: exit 1" test "$rc" -eq 1
check "start never works: files still rolled back" contains "$work/web/index.html" "OLD"
check "start never works: state records rolled-back" contains "$work/state/state" "last_result=rolled-back"
check "start never works: says Jellyfin is not answering" contains "$work/state/state" "not answering"
check "start never works: one start per attempt, no duplicate from the exit trap" test "$(grep -c '^start jellyfin' "$work/systemctl.log")" -eq 2
check "start never works: no repeated log line" test "$(grep -c 'Starting Jellyfin via systemctl' "$work/state/updater.log")" -eq 2
# A failing stop changes nothing and is recorded as failed.
reset_target; echo OLD > "$work/web/index.html"
touch "$work/stop_fail"
run_installer --auto; rc=$?
check "failing stop: exit 1" test "$rc" -eq 1
check "failing stop: files untouched" contains "$work/web/index.html" "OLD"
check "failing stop: state records failed" contains "$work/state/state" "last_result=failed"
check "failing stop: no half-done backup replaced anything" test ! -e "$work/web/.browse-modes-version"
rm -f "$work/stop_fail"

# ---- 5. auto-update switch ---------------------------------------------------
echo "== cron"
reset_target
printf '%s\n' '# keep me' '0 1 * * * /usr/bin/other-job' > "$work/crontab.txt"
run_installer --enable-auto-update; rc=$?
check "enable succeeds" test "$rc" -eq 0
check "enable adds exactly one marked line" test "$(grep -c '# browse-modes-auto-update' "$work/crontab.txt")" -eq 1
check "enable keeps other crontab lines" contains "$work/crontab.txt" "/usr/bin/other-job"
check "enable keeps comments" contains "$work/crontab.txt" "# keep me"
check "cron line runs the local copy with --auto" grep -qE "^[0-9]+ [2-5] \* \* \* $work/lib/updater.sh --auto .*# browse-modes-auto-update$" "$work/crontab.txt"
check "cron line does not use curl" lacks "$work/crontab.txt" "curl"
check "local copy installed with mode 755" bash -c "[ \"\$(stat -c %a '$work/lib/updater.sh')\" = 755 ]"
check "local copy is identical to the script" cmp -s "$script" "$work/lib/updater.sh"
first=$(grep 'browse-modes-auto-update' "$work/crontab.txt")
cp "$work/crontab.txt" "$work/crontab.before"
run_installer --enable-auto-update; rc=$?
check "enable twice succeeds" test "$rc" -eq 0
check "enable twice: still exactly one marked line" test "$(grep -c '# browse-modes-auto-update' "$work/crontab.txt")" -eq 1
check "enable twice: crontab byte-identical" cmp -s "$work/crontab.txt" "$work/crontab.before"
check "enable twice: schedule unchanged" contains "$work/crontab.txt" "$first"
check "enable twice says already enabled" contains "$work/out" "already enabled"
run_installer --status; rc=$?
check "status reports auto-update enabled" contains "$work/out" "Auto-update:          enabled"
check "status shows the running Jellyfin version" contains "$work/out" "12.3.0"
run_installer --disable-auto-update; rc=$?
check "disable succeeds" test "$rc" -eq 0
check "disable removes the marked line" lacks "$work/crontab.txt" "browse-modes-auto-update"
check "disable keeps the other job" contains "$work/crontab.txt" "/usr/bin/other-job"
check "disable keeps the comment" contains "$work/crontab.txt" "# keep me"
check "disable removes the local copy" test ! -e "$work/lib/updater.sh"
run_installer --disable-auto-update; rc=$?
check "disable twice is harmless" test "$rc" -eq 0

# Empty crontab beforehand: disable must leave it empty.
rm -f "$work/crontab.txt"
run_installer --enable-auto-update
run_installer --disable-auto-update
check "enable then disable on an empty crontab leaves it empty" test ! -s "$work/crontab.txt"

# Refusals. (Docker auto-update is covered by tests/test-docker.sh.)
PATH="$work/bin:$PATH" BM_LIB_DIR="$work/lib" BM_STATE_DIR="$work/state" BM_SKIP_ROOT_CHECK=1 \
  bash -s -- --enable-auto-update < "$script" > "$work/out" 2>&1; rc=$?
check "enable refuses when the script is piped from stdin" test "$rc" -ne 0
check "piped refusal added no cron line" test ! -s "$work/crontab.txt"
if [ "$(id -u)" -ne 0 ]; then
  BM_SKIP_ROOT_CHECK= PATH="$work/bin:$PATH" bash "$script" --enable-auto-update > "$work/out" 2>&1; rc=$?
  check "non-root enable is refused" test "$rc" -ne 0
  check "non-root enable added no cron line" test ! -s "$work/crontab.txt"
fi

echo
echo "passed: $pass  failed: $fail"
[ "$fail" -eq 0 ]
