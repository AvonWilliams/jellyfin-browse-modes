#!/usr/bin/env bash
# Tests for server discovery, selection, the plan/confirmation, the auto-update
# question and the end-of-run summary of install-browse-modes.sh. Same scratch
# setup as tests/test-docker.sh plus stub systemctl for the native install.
# Answers come from a file via the test-only BM_TTY variable.
#
# Usage: tests/test-interactive.sh
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
# shellcheck source=tests/lib-docker.sh
. "$repo/tests/lib-docker.sh"

# Stub systemctl: "cat" succeeds while $work/native_present exists, "is-active"
# while $work/native_active exists; everything is logged.
cat > "$work/bin/systemctl" <<EOF
#!/bin/sh
echo "\$*" >> "$work/systemctl.log"
case "\$1" in
  cat) [ -e "$work/native_present" ] ;;
  is-active) [ -e "$work/native_active" ] ;;
  *) exit 0 ;;
esac
EOF
chmod +x "$work/bin/systemctl"
echo native > "$work/default_ctr"

# run_ix <answers|-> <args...>: answers is a file of lines fed as terminal input
# ("-" for none). JELLYFIN_PORT points the native check at the fake server.
run_ix() {
  local ans="$1"; shift
  echo native > "$work/default_ctr"   # requests without a container header are the native install's
  local tty_env=()
  if [ "$ans" != "-" ]; then tty_env=(BM_TTY="$ans"); fi
  env PATH="$work/bin:$PATH" JELLYFIN_PORT="$port" \
    BM_RAW_BASE="file://$work/rel/raw" BM_PLUGIN_REL_BASE="file://$work/rel/plugin" \
    BM_WEB_REL_BASE="file://$work/rel/web" BM_ALLOW_LOCAL_URLS=1 \
    BM_STATE_DIR="$work/state" BM_LIB_DIR="$work/lib" BM_SKIP_ROOT_CHECK=1 \
    BM_PLUGIN_DIR="$work/plugins" BM_WEB_DIR="$work/web" \
    BM_HEALTH_TRIES=3 BM_HEALTH_INTERVAL=0 "${tty_env[@]}" \
    bash "$script" "$@" > "$work/out" 2>&1
  local rc=$?
  if [ -n "${IX_SHOW:-}" ]; then echo "---- run_ix $* (rc=$rc)"; cat "$work/out"; fi
  return $rc
}
answers() { printf '%s\n' "$@" > "$work/answers"; }

reset_all() {
  rm -rf "${work:?}/ctr" "${work:?}/vol" "${work:?}/volumes" "${work:?}/plugins" "${work:?}/web" "${work:?}/crontab.txt"
  rm -f "$work/native_present" "$work/native_active" "$work/systemctl.log"
  reset_state
}
native_up() {
  touch "$work/native_present" "$work/native_active"
  mkdir -p "$work/plugins" "$work/web"; echo NATIVESTOCK > "$work/web/index.html"
}

build_release 12.3.0 12.3.0 12.3
echo 12.3.0 > "$work/jf_version"

# ---- 1. discovery ------------------------------------------------------------
echo "== discovery"
reset_all; native_up
new_ctr jfa
IMAGE=lscr.io/linuxserver/jellyfin:latest WEBPATH=/usr/share/jellyfin/web PLUGREL=data/plugins new_ctr lsio
IMAGE=jellyfin/jellyfin:12.3 new_ctr stopped1; mk_inspect stopped1 bind 0 0
IMAGE=ghcr.io/example/custom:1 new_ctr custom
IMAGE=postgres:16 new_ctr pg; rm -rf "$work/ctr/pg/fs/jellyfin"
IMAGE=redis:7 new_ctr cache; rm -rf "$work/ctr/cache/fs/jellyfin"
echo 12.9.1 > "$work/ctr/lsio/version"
answers q
run_ix "$work/answers"; rc=$?
check "q quits with exit 0" test "$rc" -eq 0
check "q changes nothing" bash -c "grep -q NATIVESTOCK '$work/web/index.html' && grep -q STOCK '$(webdir jfa)/index.html' && [ \"\$(grep -c '^restart' '$work/docker.log')\" = 0 ]"
check "native install is listed" contains "$work/out" "1) native"
check "official container is listed" contains "$work/out" "docker: jfa (image jellyfin/jellyfin:12.3)"
check "linuxserver container is listed" contains "$work/out" "docker: lsio (image lscr.io/linuxserver/jellyfin:latest)"
check "container with another image name but a Jellyfin web root is listed" contains "$work/out" "docker: custom"
check "stopped container is listed" contains "$work/out" "docker: stopped1"
check "stopped container is marked stopped" bash -c "grep -A1 'docker: stopped1' '$work/out' | grep -q stopped"
check "stopped container note explains" contains "$work/out" "start the container to update it"
check "non-Jellyfin database container is not listed" lacks "$work/out" "docker: pg"
check "non-Jellyfin cache container is not listed" lacks "$work/out" "docker: cache"
check "server version is shown" contains "$work/out" "Jellyfin 12.3.0, running"
check "unsupported version shows the compat status" contains "$work/out" "no tested release for 12.9"
check "supported version shows the tested release" contains "$work/out" "tested release for 12.3: plugin 12.3.0, web 12.3.0"
check "installed state is shown" contains "$work/out" "Installed now: plugin 2.2.0.0, web bundle none"
check "native url is shown" contains "$work/out" "http://127.0.0.1:$port"

# ---- 2. selection parsing ----------------------------------------------------
echo "== selection"
idx_of() { grep -E "^  [0-9]+\) docker: $1 " "$work/out" | sed 's/^ *\([0-9]*\)).*/\1/'; }
i_jfa=$(idx_of jfa); i_lsio=$(idx_of lsio); i_custom=$(idx_of custom); i_stopped=$(idx_of stopped1)
check "containers are numbered after the native install" test "$i_jfa" -ge 2 -a "$i_lsio" -ge 2 -a "$i_custom" -ge 2 -a "$i_stopped" -ge 2

reset_all; native_up; new_ctr jfa; new_ctr jfb
answers 1 y n
run_ix "$work/answers"; rc=$?
check "selecting 1 updates the native install only" bash -c "grep -q 12.3.0 '$work/web/index.html' && grep -q STOCK '$(webdir jfa)/index.html' && grep -q STOCK '$(webdir jfb)/index.html'"
check "selecting 1 exits 0" test "$rc" -eq 0

reset_all; native_up; new_ctr jfa; new_ctr jfb
answers 1,3 y n
run_ix "$work/answers"
check "1,3 updates those two" bash -c "grep -q 12.3.0 '$work/web/index.html' && ! grep -q STOCK '$(webdir jfb)/index.html' && grep -q STOCK '$(webdir jfa)/index.html'"

reset_all; native_up; new_ctr jfa; new_ctr jfb
answers 1-2 y n n
run_ix "$work/answers"
check "range 1-2 updates the first two" bash -c "grep -q 12.3.0 '$work/web/index.html' && ! grep -q STOCK '$(webdir jfa)/index.html' && grep -q STOCK '$(webdir jfb)/index.html'"

reset_all; native_up; new_ctr jfa; new_ctr jfb; new_ctr stoppedx; mk_inspect stoppedx bind 0 0
answers all y n n n
run_ix "$work/answers"; rc=$?
check "all updates every running server with a tested release" bash -c "grep -q 12.3.0 '$work/web/index.html' && ! grep -q STOCK '$(webdir jfa)/index.html' && ! grep -q STOCK '$(webdir jfb)/index.html'"
check "all leaves the stopped container alone" contains "$(webdir stoppedx)/index.html" "STOCK"
check "all exits 0" test "$rc" -eq 0

reset_all; native_up; new_ctr jfa
answers x 9 0 3-1 "" "1 2" y n n
run_ix "$work/answers"
check "invalid answers are reported" test "$(grep -c "Not a valid choice" "$work/out")" -eq 5
check "an empty answer re-prompts" test "$(grep -c "Which servers should be updated" "$work/out")" -ge 6
check "a valid answer after bad ones is used" bash -c "grep -q 12.3.0 '$work/web/index.html' && ! grep -q STOCK '$(webdir jfa)/index.html'"

reset_all; native_up; new_ctr jfa
answers ""
run_ix "$work/answers"; rc=$?
check "end of input while choosing exits 1" test "$rc" -eq 1
check "end of input changes nothing" grep -q NATIVESTOCK "$work/web/index.html"

# ---- 3. one candidate, confirmation, dry run ----------------------------------
echo "== confirmation"
reset_all; native_up
answers ""
run_ix "$work/answers"; rc=$?
check "single server: empty answer means yes" grep -q 12.3.0 "$work/web/index.html"
check "single server asks to confirm" contains "$work/out" "Go ahead with this server? [Y/n]"
reset_all; native_up
answers n
run_ix "$work/answers"; rc=$?
check "single server: n changes nothing" grep -q NATIVESTOCK "$work/web/index.html"
check "single server: n says nothing was changed" contains "$work/out" "Nothing was changed"
check "plan lists the backup location" contains "$work/out" "backed up under $work/state/backups/"
check "plan names the versions" contains "$work/out" "install plugin 12.3.0 and web bundle 12.3.0 for Jellyfin 12.3"

reset_all; native_up; new_ctr jfa
answers 1-2 n
run_ix "$work/answers"
check "declining the final confirmation changes nothing" bash -c "grep -q NATIVESTOCK '$work/web/index.html' && grep -q STOCK '$(webdir jfa)/index.html'"

reset_all; native_up; new_ctr jfa
answers 1-2
run_ix "$work/answers" --dry-run; rc=$?
check "dry run shows the plan" contains "$work/out" "Plan:"
check "dry run asks only for the selection" bash -c "! grep -q 'Go ahead' '$work/out'"
check "dry run changes nothing" bash -c "grep -q NATIVESTOCK '$work/web/index.html' && grep -q STOCK '$(webdir jfa)/index.html'"
check "dry run never restarts anything" bash -c "[ ! -e '$work/systemctl.log' ] || ! grep -q '^stop' '$work/systemctl.log'"
check "dry run summary says dry run" contains "$work/out" "dry run:"

# ---- 4. no terminal, --yes, --all, --target -----------------------------------
echo "== non-interactive"
reset_all; native_up; new_ctr jfa
run_ix -; rc=$?
check "no terminal and no target: native only, with a message" bash -c "grep -q 'No terminal is available' '$work/out' && grep -q 12.3.0 '$work/web/index.html' && grep -q STOCK '$(webdir jfa)/index.html'"
reset_all; native_up; new_ctr jfa
run_ix - --yes; rc=$?
check "--yes without a terminal installs on every supported server" bash -c "grep -q 12.3.0 '$work/web/index.html' && ! grep -q STOCK '$(webdir jfa)/index.html'"
check "--yes does not ask anything" bash -c "! grep -q 'Go ahead\|Which servers\|Enable daily' '$work/out'"
check "--yes exits 0" test "$rc" -eq 0
reset_all; native_up; new_ctr jfa; new_ctr jfb; echo 12.9.1 > "$work/ctr/jfb/version"
run_ix - --all; rc=$?
check "--all skips servers without a tested release" bash -c "grep -q STOCK '$(webdir jfb)/index.html' && ! grep -q STOCK '$(webdir jfa)/index.html'"
check "--all exits 0" test "$rc" -eq 0
reset_all; native_up; new_ctr jfa; new_ctr jfb
run_ix - --target docker:jfb; rc=$?
check "--target docker:NAME touches only that container" bash -c "grep -q STOCK '$(webdir jfa)/index.html' && ! grep -q STOCK '$(webdir jfb)/index.html' && grep -q NATIVESTOCK '$work/web/index.html'"
reset_all; native_up; new_ctr jfa; new_ctr jfb
run_ix - --target native --target docker:jfa; rc=$?
check "--target is repeatable" bash -c "grep -q 12.3.0 '$work/web/index.html' && ! grep -q STOCK '$(webdir jfa)/index.html' && grep -q STOCK '$(webdir jfb)/index.html'"
run_ix - --target banana; rc=$?
check "an invalid --target is refused" test "$rc" -eq 2
reset_all; native_up
answers 1 y
run_ix "$work/answers" --auto; rc=$?
check "--auto never asks, even with a terminal" bash -c "! grep -q 'Which servers\|Go ahead' '$work/out'"
check "--auto still installs natively" grep -q 12.3.0 "$work/web/index.html"
check "--auto prints no summary" bash -c "! grep -q 'Summary' '$work/out'"

# ---- exit codes when nothing has a tested release ------------------------------
echo "== exit codes"
reset_all; native_up
echo 12.9.1 > "$work/jf_version"
answers ""
run_ix "$work/answers"; rc=$?
check "interactive: the only server has no tested release -> exit 3" test "$rc" -eq 3
check "interactive: no tested release -> nothing changed" grep -q NATIVESTOCK "$work/web/index.html"
run_ix - --yes; rc=$?
check "--yes: no server has a tested release -> exit 3" test "$rc" -eq 3
run_ix - --all; rc=$?
check "--all: no server has a tested release -> exit 3" test "$rc" -eq 3
run_ix - --target native; rc=$?
check "--target: no tested release -> exit 3 (as before)" test "$rc" -eq 3
run_ix - --auto; rc=$?
check "--auto: no tested release stays exit 0" test "$rc" -eq 0
new_ctr jfa; echo 12.9.1 > "$work/ctr/jfa/version"
answers 1-2 y
run_ix "$work/answers"; rc=$?
check "interactive: several chosen, all without a tested release -> exit 3" test "$rc" -eq 3
run_ix - --target native --target docker:jfa; rc=$?
check "--target x2: all skipped for lack of a release -> exit 3" test "$rc" -eq 3
run_ix - --auto --target native --target docker:jfa; rc=$?
check "--auto with several targets, all without a release -> exit 0" test "$rc" -eq 0
echo 12.3.0 > "$work/ctr/jfa/version"
run_ix - --target native --target docker:jfa; rc=$?
check "mix: one installed, one skipped for lack of a release -> exit 0" test "$rc" -eq 0
echo 12.3.0 > "$work/jf_version"; echo 12.9.1 > "$work/ctr/jfa/version"
run_ix - --target native --target docker:jfa; rc=$?
check "mix: native installed, container skipped -> exit 0" test "$rc" -eq 0
run_ix - --target native --target docker:jfa; rc=$?
check "mix: a rerun with one up to date and one skipped -> exit 0" test "$rc" -eq 0
reset_all; native_up; new_ctr jfa; new_ctr jfb; echo 12.9.1 > "$work/ctr/jfb/version"
touch "$work/cp_fail"
run_ix - --target docker:jfa --target docker:jfb; rc=$?
check "mix: one failed, one skipped -> exit 1" test "$rc" -eq 1
reset_all; native_up; new_ctr jfa; echo 12.9.1 > "$work/ctr/jfa/version"
run_ix - --dry-run --target native --target docker:jfa; rc=$?
check "mix: dry run of one, skipped other -> exit 0" test "$rc" -eq 0

# ---- 5. several targets, one fails -------------------------------------------
echo "== independent targets"
reset_all; native_up; new_ctr jfa; new_ctr jfb
touch "$work/cp_fail"
run_ix - --target docker:jfa --target docker:jfb --target native; rc=$?
check "a failing target makes the exit code 1" test "$rc" -eq 1
check "the failed target was restored" contains "$(webdir jfa)/index.html" "STOCK"
check "the next container still got installed" bash -c "! grep -q STOCK '$(webdir jfb)/index.html'"
check "the native install after it still got installed" grep -q 12.3.0 "$work/web/index.html"
check "summary reports the failure" contains "$work/out" "failed:"
check "summary reports the successes" bash -c "grep -cE 'Result:       (installed|updated)' '$work/out' | grep -q 2"

# ---- 6. summary content ------------------------------------------------------
echo "== summary"
reset_all; native_up; new_ctr jfa; new_ctr jfb; new_ctr jfc
echo 12.9.1 > "$work/ctr/jfc/version"
run_ix - --target native --target docker:jfa --target docker:jfc
run_ix - --target native --target docker:jfa --target docker:jfb --target docker:jfc; rc=$?
check "summary lists each target" bash -c "grep -q '^  native' '$work/out' && grep -q '^  docker:jfa' '$work/out' && grep -q '^  docker:jfc' '$work/out'"
check "summary: already up to date" contains "$work/out" "Result:       already up to date"
check "summary: updated on the container that had an older plugin" bash -c "grep -A3 '^  docker:jfb' '$work/out' | grep -q 'Result:       updated'"
check "summary: skipped when there is no tested release" contains "$work/out" "Result:       skipped: No tested Browse Modes release for Jellyfin 12.9"
check "summary: versions now installed" contains "$work/out" "Plugin now:   12.3.0    Web bundle now: 12.3.0"
check "summary: backup location" bash -c "grep -q 'Backup:       $work/state/backups/' '$work/out'"
check "summary: auto-update state" contains "$work/out" "Auto-update:  not enabled"
check "summary: log path" contains "$work/out" "Log: $work/state/updater.log"
check "summary: totals" contains "$work/out" "4 target(s): 1 installed or updated, 2 already up to date, 1 skipped, 0 failed."
check "summary: next steps mention a hard refresh" contains "$work/out" "hard refresh"
check "summary: next steps mention the backup" contains "$work/out" "backup folder"
check "no failure, exit 0" test "$rc" -eq 0
# "updated" when something was there before.
reset_all; native_up
mkdir -p "$work/plugins/Browse Modes_2.2.0.0"; echo '{"name":"Browse Modes","version":"2.2.0.0"}' > "$work/plugins/Browse Modes_2.2.0.0/meta.json"
run_ix - --native
check "summary: updated when an older plugin was installed" contains "$work/out" "Result:       updated"

# ---- 7. auto-update question -------------------------------------------------
echo "== auto-update question"
reset_all; native_up; new_ctr jfa; new_ctr jfb
answers 2-3 y y n
run_ix "$work/answers"; rc=$?
check "the auto-update question is asked per target" test "$(grep -c "Enable daily automatic updates" "$work/out")" -eq 2
check "yes adds a cron line for that target" contains "$work/crontab.txt" "docker:jfa"
check "no adds nothing for the other" lacks "$work/crontab.txt" "docker:jfb"
check "summary shows auto-update enabled for the first" bash -c "grep -A5 '^  docker:jfa' '$work/out' | grep -q 'Auto-update:  enabled'"
check "the question explains how to remove it" contains "$work/out" "Nothing is enabled unless you say yes"
reset_all; native_up; new_ctr jfa
answers 1-2 "" "" ""
run_ix "$work/answers"
check "empty answer to the auto-update question means no" test ! -s "$work/crontab.txt"

# curl | bash: the script arrives on stdin, the answers come from the terminal.
reset_all; native_up
answers ""
env PATH="$work/bin:$PATH" JELLYFIN_PORT="$port" BM_TTY="$work/answers" \
  BM_RAW_BASE="file://$work/rel/raw" BM_PLUGIN_REL_BASE="file://$work/rel/plugin" \
  BM_WEB_REL_BASE="file://$work/rel/web" BM_ALLOW_LOCAL_URLS=1 BM_STATE_DIR="$work/state" \
  BM_SKIP_ROOT_CHECK=1 BM_PLUGIN_DIR="$work/plugins" BM_WEB_DIR="$work/web" BM_HEALTH_TRIES=3 BM_HEALTH_INTERVAL=0 \
  bash -s -- < "$script" > "$work/out" 2>&1
check "piped script reads its answers from the terminal" bash -c "grep -q 'Go ahead with this server' '$work/out' && grep -q 12.3.0 '$work/web/index.html'"

echo
echo "passed: $pass  failed: $fail"
[ "$fail" -eq 0 ]
