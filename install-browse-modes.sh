#!/usr/bin/env bash
# Installs/updates the Browse Modes plugin and web bundle on a Jellyfin server.
#
# What to install is decided by compat.json in the plugin repo: it maps a
# Jellyfin major.minor to the plugin/web release that was tested against it.
# A Jellyfin version with no entry is unsupported and nothing is changed.
# Downloads are verified (plugin: MD5 from manifest.json, web: SHA-256 from
# compat.json) before any server file is touched.
#
# Ordering: the Jellyfin process is stopped before any plugin/web file is
# removed or replaced and started again afterwards. A running server holds the
# plugin DLLs open (especially on FUSE-backed config volumes under Docker),
# which makes "rm -rf" of the old plugin dir fail with "Directory not empty".
set -euo pipefail

PLUGIN_REPO="AvonWilliams/jellyfin-browse-modes"
WEB_REPO="AvonWilliams/jellyfin-web"

HOST="${JELLYFIN_HOST:-127.0.0.1}"
PORT="${JELLYFIN_PORT:-8096}"
CONTAINER=""
WEB_ONLY=0
PLUGIN_ONLY=0
ACTION="install"
AUTO=0
DRY_RUN=0

# Locations. The BM_* variables exist so the test suite (tests/) can run against
# a scratch directory and a local file:// release tree; they are not meant to be
# set in normal use.
RAW_BASE="${BM_RAW_BASE:-https://raw.githubusercontent.com/${PLUGIN_REPO}/main}"
PLUGIN_REL_BASE="${BM_PLUGIN_REL_BASE:-https://github.com/${PLUGIN_REPO}/releases/download}"
WEB_REL_BASE="${BM_WEB_REL_BASE:-https://github.com/${WEB_REPO}/releases/download}"
STATE_DIR="${BM_STATE_DIR:-/var/lib/browse-modes-updater}"
LIB_DIR="${BM_LIB_DIR:-/usr/local/lib/browse-modes}"
plugin_dir="${BM_PLUGIN_DIR:-/var/lib/jellyfin/plugins}"
web_dir="${BM_WEB_DIR:-/usr/share/jellyfin/web}"
HEALTH_TRIES="${BM_HEALTH_TRIES:-30}"
HEALTH_INTERVAL="${BM_HEALTH_INTERVAL:-2}"
CURL_PROTO="=https"
if [ -n "${BM_ALLOW_LOCAL_URLS:-}" ]; then CURL_PROTO="=https,http,file"; fi

CRON_MARKER="# browse-modes-auto-update"
KEEP_BACKUPS=3

usage() {
  cat <<'EOF'
Usage: install-browse-modes.sh [options]

Installs the Browse Modes plugin and web bundle on a Jellyfin server. The
release to install is looked up in compat.json for the running Jellyfin
major.minor (read from the server's public info endpoint). If there is no entry
for your version, nothing is changed.

Options:
  --host HOST            Jellyfin server host (default 127.0.0.1)
  --port PORT            Jellyfin server port (default 8096)
  --docker CONTAINER     Install into Docker container CONTAINER instead of a
                         native install (default: native via systemctl)
  --plugin-only          Install only the plugin, not the web bundle
  --web-only             Install only the web bundle, not the plugin
  --dry-run              Check and download+verify everything, change nothing
  --status               Show Jellyfin version, compat entry, installed
                         versions, auto-update state and the last run result
  --auto                 Non-interactive run for cron: quiet, logs to
                         /var/lib/browse-modes-updater/updater.log and syslog,
                         exits 0 when there is nothing to do
  --enable-auto-update   Install a local copy of this script and add one daily
                         root cron job that runs it with --auto (native only)
  --disable-auto-update  Remove that cron job and the local copy
  -h, --help             Show this help

Environment: JELLYFIN_HOST and JELLYFIN_PORT set the defaults for --host/--port.
Installing natively (and enabling/disabling auto-update) needs root. --status
and --dry-run do not. Docker mode needs the docker CLI and a container using
the standard /config and /jellyfin layout.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --host) HOST="${2:?--host requires a value}"; shift 2 ;;
    --port) PORT="${2:?--port requires a value}"; shift 2 ;;
    --docker) CONTAINER="${2:?--docker requires a value}"; shift 2 ;;
    --web-only) WEB_ONLY=1; shift ;;
    --plugin-only) PLUGIN_ONLY=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --auto) AUTO=1; shift ;;
    --status|--enable-auto-update|--disable-auto-update)
      if [ "$ACTION" != "install" ]; then
        echo "Only one of --status, --enable-auto-update, --disable-auto-update may be given" >&2
        exit 2
      fi
      case "$1" in
        --status) ACTION="status" ;;
        --enable-auto-update) ACTION="enable" ;;
        --disable-auto-update) ACTION="disable" ;;
      esac
      shift ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ "$WEB_ONLY" -eq 1 ] && [ "$PLUGIN_ONLY" -eq 1 ]; then
  echo "--web-only and --plugin-only are mutually exclusive" >&2
  exit 2
fi
if [[ ! "$HOST" =~ ^[A-Za-z0-9._:-]+$ ]] || [[ ! "$PORT" =~ ^[0-9]+$ ]]; then
  echo "Invalid --host or --port" >&2
  exit 2
fi
if [ "$AUTO" -eq 1 ] && [ "$ACTION" != "install" ]; then
  echo "--auto cannot be combined with --status or the auto-update switches" >&2
  exit 2
fi
if [ "$AUTO" -eq 1 ] && [ -n "$CONTAINER" ]; then
  echo "--auto is for native installs only (a container recreate wipes the web bundle)" >&2
  exit 2
fi

# Root is needed to write the Jellyfin directories, the crontab and /usr/local.
# --status and --dry-run only read, and Docker mode talks to the docker daemon.
need_root=0
case "$ACTION" in
  install) if [ -z "$CONTAINER" ] && [ "$DRY_RUN" -eq 0 ]; then need_root=1; fi ;;
  enable|disable) need_root=1 ;;
esac
if [ "$need_root" -eq 1 ] && [ "$(id -u)" -ne 0 ] && [ -z "${BM_SKIP_ROOT_CHECK:-}" ]; then
  echo "This needs root; re-run with sudo" >&2
  exit 1
fi

# ---- logging and state --------------------------------------------------------

LOG_FILE="$STATE_DIR/updater.log"
STATE_FILE="$STATE_DIR/state"
WRITE_LOG=0
if [ "$ACTION" = "install" ] && [ "$DRY_RUN" -eq 0 ] && [ -z "$CONTAINER" ]; then WRITE_LOG=1; fi

log_line() {
  if [ "$WRITE_LOG" -eq 1 ] && [ -d "$STATE_DIR" ] && [ -w "$STATE_DIR" ]; then
    printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$LOG_FILE" 2>/dev/null || true
    if [ "$(wc -l < "$LOG_FILE" 2>/dev/null || echo 0)" -gt 1000 ]; then
      tail -n 500 "$LOG_FILE" > "$LOG_FILE.tmp" 2>/dev/null && mv -f "$LOG_FILE.tmp" "$LOG_FILE" || true
    fi
  fi
  if [ "$WRITE_LOG" -eq 1 ] && [ "$AUTO" -eq 1 ] && command -v logger >/dev/null 2>&1; then
    logger -t browse-modes-updater -- "$*" 2>/dev/null || true
  fi
}

# Progress message: stdout normally, log only under --auto.
log() {
  log_line "$*"
  if [ "$AUTO" -eq 0 ]; then echo "$*"; fi
}

# Error message: always stderr (cron mails it) and the log.
err() {
  log_line "ERROR: $*"
  echo "$*" >&2
}

die() {
  err "$*"
  record_run failed "$*"
  exit 1
}

state_get() {
  [ -r "$STATE_FILE" ] || return 0
  sed -n "s/^$1=//p" "$STATE_FILE" | head -1
}

# Records the outcome of this run next to the installed versions found on disk.
# Skipped for dry runs and read-only invocations.
record_run() {
  [ "$WRITE_LOG" -eq 1 ] || return 0
  mkdir -p "$STATE_DIR" 2>/dev/null || return 0
  [ -w "$STATE_DIR" ] || return 0
  {
    printf 'last_run=%s\n' "$(date '+%Y-%m-%d %H:%M:%S')"
    printf 'last_result=%s\n' "$1"
    printf 'last_message=%s\n' "$(printf '%s' "$2" | tr '\n' ' ')"
    printf 'jellyfin=%s\n' "${version:-unknown}"
    printf 'plugin=%s\n' "$(installed_plugin_version)"
    printf 'web=%s\n' "$(installed_web_version)"
  } > "$STATE_FILE.tmp" 2>/dev/null && mv -f "$STATE_FILE.tmp" "$STATE_FILE" || true
}

# ---- helpers -----------------------------------------------------------------

fetch() {
  curl -fsSL --max-time 300 --proto "$CURL_PROTO" --proto-redir "=https" "$@"
}

# Strictly greater, by version order (sort -V).
ver_gt() {
  [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}

version=""
major_minor=""
base="http://${HOST}:${PORT}"

# Fills $version / $major_minor from the server's public info endpoint.
detect_server() {
  local info
  info=$(curl -fsSL --max-time 15 "${base}/System/Info/Public") || return 1
  version=$(printf '%s' "$info" | grep -o '"Version"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 \
    | sed 's/.*"\([^"]*\)"$/\1/' || true)
  [ -n "$version" ] || return 2
  major_minor=$(printf '%s' "$version" | cut -d. -f1,2)
  [[ "$major_minor" =~ ^[0-9]+\.[0-9]+$ ]] || return 2
}

# Version of the plugin on disk (meta.json), or empty.
installed_plugin_version() {
  local meta f
  if [ -n "$CONTAINER" ]; then
    meta=$(docker exec "$CONTAINER" sh -c 'cat /config/plugins/Browse\ Modes_*/meta.json 2>/dev/null' || true)
    printf '%s' "$meta" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1
  else
    for f in "$plugin_dir"/Browse\ Modes_*/meta.json; do
      [ -e "$f" ] || continue
      sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -1
      break
    done
  fi
}

# Version of the web bundle we installed, from the marker file written into the
# web root. A Jellyfin package upgrade replaces the web root and removes the
# marker, which is how we notice the bundle is gone.
installed_web_version() {
  if [ -n "$CONTAINER" ]; then
    docker exec "$CONTAINER" cat /jellyfin/jellyfin-web/.browse-modes-version 2>/dev/null | head -1 || true
  else
    head -1 "$web_dir/.browse-modes-version" 2>/dev/null || true
  fi
}

# Sets C_PLUGIN / C_WEB / C_WEB_SHA for $major_minor. Returns 3 when there is no
# entry, 4 when compat.json is unusable.
C_PLUGIN=""; C_WEB=""; C_WEB_SHA=""
load_compat() {
  local json out rc=0
  json=$(fetch "${RAW_BASE}/compat.json$(raw_cache_buster)") || return 4
  out=$(printf '%s' "$json" | python3 -I -c '
import json, re, sys
try:
    d = json.load(sys.stdin)
except ValueError:
    sys.exit(4)
if d.get("schema") != 1 or not isinstance(d.get("releases"), dict):
    sys.exit(4)
e = d["releases"].get(sys.argv[1])
if not isinstance(e, dict):
    sys.exit(3)
ver = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
vals = [str(e.get(k, "-")) for k in ("plugin", "web", "webSha256")]
for v in vals[:2]:
    if v != "-" and not ver.match(v):
        sys.exit(4)
print("\t".join(vals))
' "$major_minor") || rc=$?
  [ "$rc" -eq 0 ] || return "$rc"
  IFS=$'\t' read -r C_PLUGIN C_WEB C_WEB_SHA <<< "$out"
  [ "$C_PLUGIN" != "-" ] || return 4
  return 0
}

raw_cache_buster() {
  case "$RAW_BASE" in https://*) printf '?t=%s' "$(date +%s)" ;; esac
}

# ---- status ------------------------------------------------------------------

cron_line() {
  command -v crontab >/dev/null 2>&1 || return 0
  crontab -l 2>/dev/null | grep -F -- "$CRON_MARKER" | head -1 || true
}

do_status() {
  local rc=0 line ip iw
  echo "Browse Modes updater status"
  if detect_server; then
    echo "  Jellyfin running:     ${version} (${base})"
  else
    echo "  Jellyfin running:     unreachable at ${base}"
  fi
  if [ -n "$major_minor" ]; then
    rc=0; load_compat || rc=$?
    case "$rc" in
      0) echo "  compat.json entry:    ${major_minor} -> plugin ${C_PLUGIN}, web ${C_WEB} (tested release)" ;;
      3) echo "  compat.json entry:    none for ${major_minor} (unsupported: updater does nothing)" ;;
      *) echo "  compat.json entry:    unavailable (could not fetch or parse compat.json)" ;;
    esac
  fi
  ip=$(installed_plugin_version); iw=$(installed_web_version)
  echo "  Installed plugin:     ${ip:-not installed}"
  echo "  Installed web bundle: ${iw:-not installed (or not installed by this script)}"
  line=$(cron_line)
  if [ -n "$line" ]; then
    echo "  Auto-update:          enabled"
    echo "    cron: $line"
  else
    echo "  Auto-update:          disabled"
  fi
  if [ -n "$(state_get last_run)" ]; then
    echo "  Last run:             $(state_get last_run) - $(state_get last_result): $(state_get last_message)"
  else
    echo "  Last run:             none recorded"
  fi
}

# ---- auto-update switch ------------------------------------------------------

do_enable() {
  local self src_sum min hour existing args new_line current others
  if [ -n "$CONTAINER" ]; then
    cat >&2 <<'EOF'
Auto-update is for native installs only. In Docker, recreating the container
(an image pull or "docker compose up") discards a web bundle copied into it, so
an updater inside or beside the container cannot keep the bundle in place.
Bind-mount the bundle over the web root instead (see the README) and re-run
this script manually with --docker when you update.
EOF
    exit 2
  fi
  command -v crontab >/dev/null 2>&1 || { echo "crontab not found; install cron first" >&2; exit 1; }
  for cmd in curl unzip python3 flock md5sum sha256sum; do
    command -v "$cmd" >/dev/null 2>&1 || echo "Warning: required command not found: $cmd (the updater will fail without it)" >&2
  done

  self="${BASH_SOURCE[0]}"
  if [ ! -f "$self" ] || [ ! -r "$self" ]; then
    cat >&2 <<'EOF'
Run this from a saved copy of the script, not piped from curl: the copy you
inspected is the one that gets installed. For example:
  curl -fsSL -o install-browse-modes.sh <url>
  less install-browse-modes.sh
  sudo bash install-browse-modes.sh --enable-auto-update
EOF
    exit 1
  fi
  if [[ ! "$LIB_DIR" =~ ^/[A-Za-z0-9/_.-]+$ ]]; then
    echo "Refusing to use install path with unusual characters: ${LIB_DIR}" >&2
    exit 1
  fi
  bash -n "$self" || { echo "Syntax check of ${self} failed" >&2; exit 1; }

  mkdir -p "$LIB_DIR" "$STATE_DIR"
  chmod 755 "$LIB_DIR"
  if [ "$(id -u)" -eq 0 ]; then chown root:root "$LIB_DIR"; fi
  if ! cmp -s "$self" "$LIB_DIR/updater.sh"; then
    local tmpcopy
    tmpcopy=$(mktemp "$LIB_DIR/.updater.XXXXXX")
    cp "$self" "$tmpcopy"
    chmod 755 "$tmpcopy"
    if [ "$(id -u)" -eq 0 ]; then chown root:root "$tmpcopy"; fi
    mv -f "$tmpcopy" "$LIB_DIR/updater.sh"
  fi
  src_sum=$(sha256sum "$LIB_DIR/updater.sh" | cut -d' ' -f1)

  # Options baked into the cron line: only non-default host/port, validated
  # above so nothing can break out of the line.
  args="--auto"
  if [ "$HOST" != "127.0.0.1" ]; then args="$args --host $HOST"; fi
  if [ "$PORT" != "8096" ]; then args="$args --port $PORT"; fi

  current=$(crontab -l 2>/dev/null || true)
  existing=$(printf '%s\n' "$current" | grep -F -- "$CRON_MARKER" | head -1 || true)
  if [[ "$existing" =~ ^([0-9]+)[[:space:]]+([0-9]+)[[:space:]] ]]; then
    # Keep the schedule picked the first time so re-running does not churn it.
    min="${BASH_REMATCH[1]}"; hour="${BASH_REMATCH[2]}"
  else
    min=$((RANDOM % 60)); hour=$((2 + RANDOM % 4))
  fi
  new_line="$min $hour * * * $LIB_DIR/updater.sh $args $CRON_MARKER"

  if [ "$(printf '%s\n' "$current" | grep -cF -- "$CRON_MARKER" || true)" = "1" ] && [ "$existing" = "$new_line" ]; then
    echo "Auto-update was already enabled; nothing added to the crontab."
  else
    others=$(printf '%s\n' "$current" | grep -vF -- "$CRON_MARKER" || true)
    if [ -n "$others" ]; then
      printf '%s\n%s\n' "$others" "$new_line" | crontab -
    else
      printf '%s\n' "$new_line" | crontab -
    fi
    echo "Added this line to root's crontab:"
  fi
  echo "  $new_line"
  cat <<EOF

Installed local copy: ${LIB_DIR}/updater.sh (root-owned, sha256 ${src_sum})
It runs once a day, between 02:00 and 05:59, and only changes anything when
compat.json lists a tested release for your Jellyfin version. Results go to
${LOG_FILE} and syslog.

To check on it:   sudo bash ${LIB_DIR}/updater.sh --status
To remove it:     sudo bash ${LIB_DIR}/updater.sh --disable-auto-update
Or by hand:       sudo crontab -l | grep -vF '${CRON_MARKER}' | sudo crontab -
                  sudo rm -rf ${LIB_DIR}
EOF
}

do_disable() {
  local current others had
  command -v crontab >/dev/null 2>&1 || { echo "crontab not found" >&2; exit 1; }
  current=$(crontab -l 2>/dev/null || true)
  had=$(printf '%s\n' "$current" | grep -cF -- "$CRON_MARKER" || true)
  if [ "$had" != "0" ]; then
    others=$(printf '%s\n' "$current" | grep -vF -- "$CRON_MARKER" || true)
    if [ -n "$others" ]; then
      printf '%s\n' "$others" | crontab -
    else
      printf '' | crontab -
    fi
    echo "Removed the auto-update line from root's crontab."
  else
    echo "No auto-update line found in root's crontab."
  fi
  if [ -e "$LIB_DIR/updater.sh" ]; then
    rm -f "$LIB_DIR/updater.sh"
    rmdir "$LIB_DIR" 2>/dev/null || true
    echo "Removed ${LIB_DIR}/updater.sh."
  fi
  echo "Left in place: ${STATE_DIR} (log and state) and any backups in it; delete it yourself if you want."
}

case "$ACTION" in
  enable) do_enable; exit 0 ;;
  disable) do_disable; exit 0 ;;
  status) do_status; exit 0 ;;
esac

# ---- install -----------------------------------------------------------------

for cmd in curl unzip python3 md5sum sha256sum; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Required command not found: $cmd" >&2; exit 1; }
done
if [ -n "$CONTAINER" ] && ! command -v docker >/dev/null 2>&1; then
  echo "docker not found (required by --docker)" >&2
  exit 1
fi

# One run at a time (cron vs. a manual run).
if [ "$DRY_RUN" -eq 0 ] && [ -z "$CONTAINER" ]; then
  mkdir -p "$STATE_DIR"
  command -v flock >/dev/null 2>&1 || { echo "Required command not found: flock" >&2; exit 1; }
  exec 9>"$STATE_DIR/lock"
  if ! flock -n 9; then
    log "Another Browse Modes update is already running; exiting."
    exit 0
  fi
fi

tmp=$(mktemp -d)
stopped=0

# Start the server again after we stopped it. Idempotent: only acts when
# $stopped is 1, so the EXIT trap can call it safely on every exit path.
start_server() {
  if [ "$stopped" -ne 1 ]; then
    return 0
  fi
  if [ -n "$CONTAINER" ]; then
    log "Starting container ${CONTAINER}"
    docker start "$CONTAINER" >/dev/null
  else
    log "Starting Jellyfin via systemctl"
    systemctl start jellyfin
  fi
  stopped=0
}

stop_server() {
  if [ -n "$CONTAINER" ]; then
    log "Stopping container ${CONTAINER}"
    docker stop "$CONTAINER" >/dev/null
  else
    log "Stopping Jellyfin via systemctl"
    systemctl stop jellyfin
  fi
  stopped=1
}

cleanup() {
  # Safety net: never leave the server stopped if a later step failed.
  start_server || true
  rm -rf "$tmp"
}
trap cleanup EXIT

# Polls the public info endpoint until the server answers (default ~60s).
health_check() {
  local i
  for i in $(seq 1 "$HEALTH_TRIES"); do
    if curl -fsS --max-time 5 "${base}/System/Info/Public" 2>/dev/null | grep -q '"Version"'; then
      return 0
    fi
    sleep "$HEALTH_INTERVAL"
  done
  return 1
}

# ---- decide what applies ----
rc=0; detect_server || rc=$?
case "$rc" in
  0) ;;
  1) die "Could not reach Jellyfin server at ${base}" ;;
  *) die "Could not determine the Jellyfin version from ${base}/System/Info/Public" ;;
esac
log "Detected Jellyfin ${version}"

rc=0; load_compat || rc=$?
case "$rc" in
  0) ;;
  3)
    msg="No tested Browse Modes release for Jellyfin ${major_minor} (not listed in compat.json); nothing changed"
    log "$msg"
    record_run no-entry "$msg"
    if [ "$AUTO" -eq 1 ]; then exit 0; fi
    exit 3 ;;
  *) die "Could not read compat.json from ${RAW_BASE} (fetch or format error); nothing changed" ;;
esac
log "Tested release for Jellyfin ${major_minor}: plugin ${C_PLUGIN}, web ${C_WEB}"

plugin_update=0
web_update=0
if [ "$PLUGIN_ONLY" -eq 0 ] && [ "$C_WEB" = "-" ]; then
  log "compat.json lists no web bundle for Jellyfin ${major_minor}; skipping web."
fi
if [ "$WEB_ONLY" -eq 0 ]; then
  installed_plugin=$(installed_plugin_version)
  if [ "$installed_plugin" = "$C_PLUGIN" ]; then
    log "Plugin is already at ${C_PLUGIN}."
  elif [ -n "$installed_plugin" ] && ver_gt "$installed_plugin" "$C_PLUGIN"; then
    log "Installed plugin ${installed_plugin} is newer than the tested ${C_PLUGIN}; not downgrading."
  else
    plugin_update=1
  fi
fi
if [ "$PLUGIN_ONLY" -eq 0 ] && [ "$C_WEB" != "-" ]; then
  installed_web=$(installed_web_version)
  if [ "$installed_web" = "$C_WEB" ]; then
    log "Web bundle is already at ${C_WEB}."
  elif [ -n "$installed_web" ] && ver_gt "$installed_web" "$C_WEB"; then
    log "Installed web bundle ${installed_web} is newer than the tested ${C_WEB}; not downgrading."
  else
    web_update=1
  fi
fi

if [ "$plugin_update" -eq 0 ] && [ "$web_update" -eq 0 ]; then
  log "Already up to date; nothing to install."
  record_run noop "Already up to date (Jellyfin ${version})"
  exit 0
fi

# ---- fetch and verify everything before touching the server ----
if [ "$plugin_update" -eq 1 ]; then
  abi="${major_minor}.0"
  expected_url="${PLUGIN_REL_BASE}/v${C_PLUGIN}/browse-modes_${C_PLUGIN}_${abi}.zip"
  manifest=$(fetch "${RAW_BASE}/manifest.json$(raw_cache_buster)") || die "Failed to fetch the plugin manifest"
  entry=$(printf '%s' "$manifest" | python3 -I -c '
import json, sys
m = json.load(sys.stdin)
for v in m[0]["versions"]:
    if v.get("version") == sys.argv[1] and v.get("targetAbi") == sys.argv[2] + ".0":
        print(v.get("sourceUrl", "-") + "\t" + v.get("checksum", "-")); break
else:
    sys.exit(1)
' "$C_PLUGIN" "$abi") || die "manifest.json has no plugin ${C_PLUGIN} entry for targetAbi ${abi}.0; nothing changed"
  IFS=$'\t' read -r asset_url checksum <<< "$entry"
  [ "$asset_url" = "$expected_url" ] || die "Manifest URL for plugin ${C_PLUGIN} is not the expected release asset; refusing"
  [[ "$checksum" =~ ^[0-9a-f]{32}$ ]] || die "Manifest has no usable MD5 for plugin ${C_PLUGIN} (${checksum}); refusing to install"

  log "Downloading $(basename "$asset_url")"
  fetch -o "$tmp/plugin.zip" "$asset_url" || die "Download failed: ${asset_url}"
  printf '%s  %s\n' "$checksum" "$tmp/plugin.zip" | md5sum -c - >/dev/null \
    || die "Checksum mismatch for $(basename "$asset_url"); nothing changed"

  # The zip contains the .dlls and meta.json at its top level (no wrapper
  # dir), so extract to a staging dir and place it into a <name>_<version>
  # folder.
  mkdir -p "$tmp/plugin_stage"
  unzip -q -o "$tmp/plugin.zip" -d "$tmp/plugin_stage"
  name=$(sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$tmp/plugin_stage/meta.json" | head -1)
  pver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$tmp/plugin_stage/meta.json" | head -1)
  pabi=$(sed -n 's/.*"targetAbi"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$tmp/plugin_stage/meta.json" | head -1)
  if [ -z "$name" ] || [ -z "$pver" ]; then
    die "Plugin meta.json is missing name/version; cannot determine install directory"
  fi
  [ "$pabi" = "${abi}.0" ] || die "Plugin package targets ${pabi:-unknown}, not Jellyfin ${major_minor}; refusing"
  [ "$pver" = "$C_PLUGIN" ] || die "Plugin package is version ${pver}, expected ${C_PLUGIN}; refusing"
  pdir="${name}_${pver}"
fi

if [ "$web_update" -eq 1 ]; then
  web_sha="$C_WEB_SHA"
  [[ "$web_sha" =~ ^[0-9a-f]{64}$ ]] || die "compat.json has no usable SHA-256 for web ${C_WEB} (${web_sha}); refusing to install"
  # A bundle from the current 12.x scheme must belong to this Jellyfin version.
  if [ "${C_WEB%%.*}" -ge 12 ] && [ "$(printf '%s' "$C_WEB" | cut -d. -f1,2)" != "$major_minor" ]; then
    die "Web bundle ${C_WEB} does not belong to Jellyfin ${major_minor}; refusing"
  fi
  web_url="${WEB_REL_BASE}/v${C_WEB}-web/jellyfin-web-browse-modes-${C_WEB}.zip"
  log "Downloading $(basename "$web_url")"
  fetch -o "$tmp/web.zip" "$web_url" || die "Download failed: ${web_url}"
  printf '%s  %s\n' "$web_sha" "$tmp/web.zip" | sha256sum -c - >/dev/null \
    || die "SHA-256 mismatch for $(basename "$web_url"); nothing changed"
  mkdir -p "$tmp/web_stage"
  unzip -q -o "$tmp/web.zip" -d "$tmp/web_stage"
  [ -f "$tmp/web_stage/index.html" ] || die "Web bundle has no index.html at its top level; refusing"
  printf '%s\n' "$C_WEB" > "$tmp/web_stage/.browse-modes-version"
fi

if [ "$DRY_RUN" -eq 1 ]; then
  echo "Dry run: verified downloads for Jellyfin ${version}. Would install:"
  if [ -n "$CONTAINER" ]; then
    dest_plugin="container ${CONTAINER}:/config/plugins"; dest_web="container ${CONTAINER}:/jellyfin/jellyfin-web"
  else
    dest_plugin="$plugin_dir"; dest_web="$web_dir"
  fi
  if [ "$plugin_update" -eq 1 ]; then echo "  plugin ${C_PLUGIN} (md5 ok) into ${dest_plugin}/${pdir}"; fi
  if [ "$web_update" -eq 1 ]; then echo "  web bundle ${C_WEB} (sha256 ok) into ${dest_web}"; fi
  echo "Nothing was changed."
  exit 0
fi

# ---- Docker (manual installs only) ----
if [ -n "$CONTAINER" ]; then
  stop_server
  if [ "$plugin_update" -eq 1 ]; then
    docker exec "$CONTAINER" mkdir -p /config/plugins
    # Plugin dir name contains a space; escape it for the container's sh.
    docker exec "$CONTAINER" sh -c 'rm -rf /config/plugins/Browse\ Modes_*'
    docker cp "$tmp/plugin_stage" "$CONTAINER:/config/plugins/${pdir}"
    # docker cp preserves the host uid, which may not match the container's jellyfin
    # user (uid 1000, no named user). Chown to the owner of /config so meta.json stays writable.
    jf_owner=$(docker exec "$CONTAINER" sh -c 'stat -c "%u:%g" /config')
    docker exec "$CONTAINER" chown -R "$jf_owner" "/config/plugins/${pdir}"
    log "Installed plugin to container ${CONTAINER}:/config/plugins/${pdir}"
  fi
  if [ "$web_update" -eq 1 ]; then
    ts=$(date +%Y%m%d%H%M%S)
    docker exec "$CONTAINER" sh -c "mkdir -p /jellyfin; if [ -d /jellyfin/jellyfin-web ]; then mv /jellyfin/jellyfin-web /jellyfin/jellyfin-web.bak.${ts}; fi"
    docker cp "$tmp/web_stage" "$CONTAINER:/jellyfin/jellyfin-web"
    jf_owner=$(docker exec "$CONTAINER" sh -c 'stat -c "%u:%g" /config')
    docker exec "$CONTAINER" chown -R "$jf_owner" "/jellyfin/jellyfin-web"
    log "Installed web bundle to container ${CONTAINER}:/jellyfin/jellyfin-web (backup: jellyfin-web.bak.${ts})"
  fi
  start_server
  if health_check; then
    log "Server is back up. Done."
  else
    err "Server did not answer after the update. Check 'docker logs ${CONTAINER}'; the previous web bundle is in /jellyfin/jellyfin-web.bak.*"
    exit 1
  fi
  exit 0
fi

# ---- native install: back up, replace, health-check, roll back on failure ----
backup="$STATE_DIR/backups/$(date +%Y%m%d%H%M%S)"
had_web=0

apply_native() {
  local d
  mkdir -p "$backup/plugin" || return 1
  if [ -d "$web_dir" ]; then
    cp -a "$web_dir" "$backup/web" || return 1
    had_web=1
  fi
  for d in "$plugin_dir"/Browse\ Modes_*; do
    [ -e "$d" ] || continue
    cp -a "$d" "$backup/plugin/" || return 1
  done

  if [ "$plugin_update" -eq 1 ]; then
    rm -rf "$plugin_dir/Browse Modes_"* || return 1
    mkdir -p "$plugin_dir/$pdir" || return 1
    cp -a "$tmp/plugin_stage/." "$plugin_dir/$pdir/" || return 1
    # Jellyfin runs as the jellyfin user and rewrites meta.json, so give it ownership.
    if id -u jellyfin >/dev/null 2>&1; then chown -R jellyfin:jellyfin "$plugin_dir/$pdir" || return 1; fi
    log "Installed plugin ${C_PLUGIN} to ${plugin_dir}/${pdir}"
  fi
  if [ "$web_update" -eq 1 ]; then
    rm -rf "$web_dir" || return 1
    mkdir -p "$web_dir" || return 1
    cp -a "$tmp/web_stage/." "$web_dir/" || return 1
    if id -u jellyfin >/dev/null 2>&1; then chown -R jellyfin:jellyfin "$web_dir" || return 1; fi
    log "Installed web bundle ${C_WEB} to ${web_dir}"
  fi
}

# Puts back exactly what the backup holds for the parts this run replaced.
restore_native() {
  if [ "$plugin_update" -eq 1 ]; then
    rm -rf "$plugin_dir/Browse Modes_"*
    cp -a "$backup/plugin/." "$plugin_dir/"
  fi
  if [ "$web_update" -eq 1 ]; then
    rm -rf "$web_dir"
    if [ "$had_web" -eq 1 ]; then cp -a "$backup/web" "$web_dir"; fi
  fi
}

prune_backups() {
  local d
  # shellcheck disable=SC2012
  { ls -1dt "$STATE_DIR"/backups/[0-9]* 2>/dev/null || true; } | tail -n +$((KEEP_BACKUPS + 1)) | while read -r d; do
    rm -rf "$d"
  done
}

stop_server
if ! apply_native; then
  err "Applying the update failed; restoring the previous files from ${backup}"
  restore_native || true
  start_server || true
  die "Update failed and was rolled back (backup kept in ${backup})"
fi

start_server
if health_check; then
  log "Jellyfin is healthy after the update."
  prune_backups
  msg="Installed${plugin_update:+ plugin ${C_PLUGIN}}${web_update:+ web ${C_WEB}} for Jellyfin ${version}"
  log "$msg"
  record_run ok "$msg"
  log "Done. (backup of the previous files: ${backup})"
  exit 0
fi

err "Jellyfin did not come back after the update; rolling back from ${backup}"
stop_server || true
restore_native || true
start_server || true
if health_check; then
  msg="Update failed health check; rolled back to the previous files (backup kept in ${backup})"
else
  msg="Update failed health check and Jellyfin is still not answering after rollback; check 'journalctl -u jellyfin' (backup in ${backup})"
fi
err "$msg"
record_run rolled-back "$msg"
exit 1
