#!/usr/bin/env bash
# Installs/updates the Browse Modes plugin and web bundle on a Jellyfin server.
#
# What to install is decided by compat.json in the plugin repo: it maps a
# Jellyfin major.minor to the plugin/web release that was tested against it.
# A Jellyfin version with no entry is unsupported and nothing is changed.
# Downloads are verified (plugin: MD5 from manifest.json, web: SHA-256 from
# compat.json) before any server file is touched.
#
# Ordering (native): the Jellyfin process is stopped before any plugin/web file
# is removed or replaced and started again afterwards. A running server holds
# the plugin DLLs open, which makes "rm -rf" of the old plugin dir fail with
# "Directory not empty".
#
# Docker (--docker NAME): the bundle is copied into the running container and
# the container is restarted. Nothing about how the container was created
# changes, so recreating it (image update) discards the web bundle; the next
# --auto run puts it back when compat.json has an entry for the new version.
set -euo pipefail

PLUGIN_REPO="AvonWilliams/jellyfin-browse-modes"
WEB_REPO="AvonWilliams/jellyfin-web"

HOST="${JELLYFIN_HOST:-127.0.0.1}"
PORT="${JELLYFIN_PORT:-8096}"
# --host/--port given on the command line: Docker targets are then queried at
# that URL from the host instead of asking the container itself, and no server
# discovery is done.
ADDR_SET=0
CONTAINER=""       # set by set_target for the target being worked on
TARGETS=()         # explicit targets: "native" or "docker:NAME"
ALL=0
YES=0
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

Installs the Browse Modes plugin and web bundle on Jellyfin servers. The
release to install is looked up in compat.json for each server's Jellyfin
major.minor (read from its public info endpoint). If there is no entry for the
version, nothing is changed on that server.

With no target given, the script looks for Jellyfin on this machine (a native
install and Docker containers, running or stopped), lists what it found, lets
you pick one or several, shows the plan and asks before changing anything.
Questions are read from the terminal, so this also works through "curl | bash".

Choosing servers without questions:
  --target native|docker:NAME  Work on this target; repeatable
  --native                     Same as --target native
  --docker CONTAINER           Same as --target docker:CONTAINER
  --all                        Every discovered server that has a tested release
  -y, --yes                    Accept the defaults without asking (all discovered
                               servers with a tested release); no auto-update
                               question is asked

Other options:
  --host HOST            Jellyfin server host (default 127.0.0.1). With a Docker
                         target this makes the script query that address from
                         the host instead of asking the container.
  --port PORT            Jellyfin server port (default 8096)
  --plugin-only          Install only the plugin, not the web bundle
  --web-only             Install only the web bundle, not the plugin
  --dry-run              Show the plan, check and download+verify everything,
                         change nothing
  --status               Show Jellyfin version, compat entry, installed
                         versions, auto-update state and the last run result
                         for one target
  --auto                 Non-interactive run for cron: quiet, logs to
                         /var/lib/browse-modes-updater/updater.log and syslog,
                         exits 0 when there is nothing to do (also when the
                         Docker container is missing or stopped). Never asks.
  --enable-auto-update   Install a local copy of this script and add one daily
                         cron job that runs it with --auto. With --docker or
                         --target docker:NAME, one job per container (the
                         invoking user needs docker access)
  --disable-auto-update  Remove that cron job (only the one for the given
                         container, or the native one without a target); the
                         local copy goes when no browse-modes job is left
  --all                  With --disable-auto-update: remove every browse-modes
                         job (native and all containers)
  -h, --help             Show this help

Every install run ends with a summary per server (result, versions now
installed, backup location, auto-update state). The exit code is nonzero when
a server failed; a run for one server with no tested release exits 3.

Environment: JELLYFIN_HOST and JELLYFIN_PORT set the defaults for --host/--port.
A native install (and its auto-update job) needs root. --status and --dry-run
do not. Docker targets need the docker CLI, permission to use it (root or the
docker group) and a container with a Jellyfin web root (official image:
/jellyfin/jellyfin-web, linuxserver image: /usr/share/jellyfin/web or
/usr/lib/jellyfin/bin/jellyfin-web). Podman is not searched.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --host) HOST="${2:?--host requires a value}"; ADDR_SET=1; shift 2 ;;
    --port) PORT="${2:?--port requires a value}"; ADDR_SET=1; shift 2 ;;
    --docker) TARGETS+=("docker:${2:?--docker requires a value}"); shift 2 ;;
    --native) TARGETS+=("native"); shift ;;
    --target) TARGETS+=("${2:?--target requires a value}"); shift 2 ;;
    -y|--yes) YES=1; shift ;;
    --web-only) WEB_ONLY=1; shift ;;
    --plugin-only) PLUGIN_ONLY=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --auto) AUTO=1; shift ;;
    --all) ALL=1; shift ;;
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
for t in ${TARGETS[@]+"${TARGETS[@]}"}; do
  if [ "$t" != "native" ] && [[ ! "$t" =~ ^docker:[A-Za-z0-9][A-Za-z0-9_.-]*$ ]]; then
    echo "Invalid target: ${t} (use native or docker:NAME)" >&2
    exit 2
  fi
done
if [ "${#TARGETS[@]}" -gt 1 ]; then
  # Drop repeats, keep the order given.
  uniq_targets=()
  for t in "${TARGETS[@]}"; do
    case " ${uniq_targets[*]:-} " in *" $t "*) ;; *) uniq_targets+=("$t") ;; esac
  done
  TARGETS=("${uniq_targets[@]}")
fi
if [ "$ACTION" != "install" ] && [ "${#TARGETS[@]}" -gt 1 ]; then
  echo "--status and the auto-update switches work on one target at a time" >&2
  exit 2
fi
if [ "$ALL" -eq 1 ]; then
  if [ "$ACTION" = "disable" ] && [ "${#TARGETS[@]}" -gt 0 ]; then
    echo "--all removes every job; it cannot be combined with --docker, --native or --target" >&2
    exit 2
  elif [ "$ACTION" = "install" ] && [ "${#TARGETS[@]}" -gt 0 ]; then
    echo "--all cannot be combined with --docker, --native or --target" >&2
    exit 2
  elif [ "$ACTION" != "install" ] && [ "$ACTION" != "disable" ]; then
    echo "--all goes with an install run or --disable-auto-update" >&2
    exit 2
  fi
fi

ALL_TARGETS=0
if [ "$ACTION" = "install" ] && [ "$ALL" -eq 1 ]; then ALL_TARGETS=1; ALL=0; fi

# ---- targets ------------------------------------------------------------------

STATE_DIR_DEFAULT="$STATE_DIR"
LIB_DIR_DEFAULT="$LIB_DIR"
LOG_FILE=""; STATE_FILE=""; LOG_TAG=""; base=""
version=""; major_minor=""

# Points all per-target settings at one target ("native" or "docker:NAME").
set_target() {
  STATE_DIR="$STATE_DIR_DEFAULT"; LIB_DIR="$LIB_DIR_DEFAULT"
  case "$1" in
    docker:*) CONTAINER="${1#docker:}" ;;
    *) CONTAINER="" ;;
  esac
  # Without root, Docker mode keeps its state and local copy under the user's home.
  if [ -n "$CONTAINER" ] && [ "$(id -u)" -ne 0 ]; then
    if [ -z "${BM_STATE_DIR:-}" ]; then STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/browse-modes-updater"; fi
    if [ -z "${BM_LIB_DIR:-}" ]; then LIB_DIR="$HOME/.local/lib/browse-modes"; fi
  fi
  LOG_FILE="$STATE_DIR/updater.log"
  STATE_FILE="$STATE_DIR/state"
  LOG_TAG=""
  base="http://${HOST}:${PORT}"
  if [ -n "$CONTAINER" ]; then
    STATE_FILE="$STATE_DIR/state.docker-$CONTAINER"
    LOG_TAG="[docker:${CONTAINER}] "
    if [ "$ADDR_SET" -eq 0 ]; then base="container ${CONTAINER}"; fi
  fi
  version=""; major_minor=""
  DK_WEB=""; DK_CFG_HOST=""; DK_NETMODE=""; DK_MOUNTS=""; DK_PLUGINS="plugins"
}

# Root is needed to edit root's crontab and /usr/local for the native job. The
# docker jobs and --all work on the invoking user's own crontab.
if [ "$ACTION" = "enable" ] || [ "$ACTION" = "disable" ]; then
  if { [ "${#TARGETS[@]}" -eq 0 ] || [ "${TARGETS[0]}" = "native" ]; } && [ "$ALL" -eq 0 ] \
    && [ "$(id -u)" -ne 0 ] && [ -z "${BM_SKIP_ROOT_CHECK:-}" ]; then
    echo "This needs root; re-run with sudo" >&2
    exit 1
  fi
fi
set_target "${TARGETS[0]:-native}"

# ---- logging and state --------------------------------------------------------

WRITE_LOG=0
if [ "$ACTION" = "install" ] && [ "$DRY_RUN" -eq 0 ]; then WRITE_LOG=1; fi

log_line() {
  if [ "$WRITE_LOG" -eq 1 ] && [ -d "$STATE_DIR" ] && [ -w "$STATE_DIR" ]; then
    printf '%s %s%s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$LOG_TAG" "$*" >> "$LOG_FILE" 2>/dev/null || true
    if [ "$(wc -l < "$LOG_FILE" 2>/dev/null || echo 0)" -gt 1000 ]; then
      tail -n 500 "$LOG_FILE" > "$LOG_FILE.tmp" 2>/dev/null && mv -f "$LOG_FILE.tmp" "$LOG_FILE" || true
    fi
  fi
  if [ "$WRITE_LOG" -eq 1 ] && [ "$AUTO" -eq 1 ] && command -v logger >/dev/null 2>&1; then
    logger -t browse-modes-updater -- "${LOG_TAG}$*" 2>/dev/null || true
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

# Hands the outcome of this target to the summary (RESULT_FILE is only set when
# a target is run by the dispatcher).
set_result() {
  [ -n "${RESULT_FILE:-}" ] || return 0
  {
    printf 'result=%s\n' "$1"
    printf 'message=%s\n' "$(printf '%s' "$2" | tr '\n' ' ')"
    printf 'jellyfin=%s\n' "${version:-unknown}"
    printf 'prev_plugin=%s\n' "${prev_plugin:-}"
    printf 'prev_web=%s\n' "${prev_web:-}"
    printf 'plugin=%s\n' "$(installed_plugin_version || true)"
    printf 'web=%s\n' "$(installed_web_version || true)"
    printf 'backup=%s\n' "${backup:-}"
  } > "$RESULT_FILE" 2>/dev/null || true
}

# Records the outcome of this run next to the installed versions found on disk.
# Skipped for dry runs and read-only invocations.
record_run() {
  set_result "$1" "$2"
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

# Prints the server's public info JSON. Native and --host/--port runs ask the
# address from the host. Docker mode otherwise asks the container itself (works
# for bridge, host and unpublished networking alike), then tries the published
# port from the host.
jf_info() {
  local url="http://127.0.0.1:8096/System/Info/Public" pub
  if [ -z "$CONTAINER" ] || [ "$ADDR_SET" -eq 1 ]; then
    curl -fsSL --max-time 15 "${base}/System/Info/Public"
    return
  fi
  docker exec "$CONTAINER" curl -fsS --max-time 10 "$url" 2>/dev/null && return 0
  docker exec "$CONTAINER" wget -qO- -T 10 "$url" 2>/dev/null && return 0
  pub=$(dk_published_base) || return 1
  curl -fsS --max-time 10 "${pub}/System/Info/Public" 2>/dev/null
}

# Host-side base URL for the container's port 8096: the published port, or
# 127.0.0.1:8096 for host networking.
dk_published_base() {
  local line host
  if [ "${DK_NETMODE:-}" = "host" ]; then printf 'http://127.0.0.1:8096'; return 0; fi
  line=$(docker port "$CONTAINER" 8096/tcp 2>/dev/null | head -1) || return 1
  [ -n "$line" ] || return 1
  host="${line%:*}"
  case "$host" in 0.0.0.0|::|"[::]") host="127.0.0.1" ;; esac
  printf 'http://%s:%s' "$host" "${line##*:}"
}

# Fills $version / $major_minor from the server's public info endpoint.
detect_server() {
  local info
  info=$(jf_info) || return 1
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
    f=$(dk_plugin_dirs | head -1)
    [ -n "$f" ] || return 0
    meta=$(dk_plugin_meta "$f" || true)
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
    [ -n "$DK_WEB" ] || return 0
    docker exec "$CONTAINER" cat "$DK_WEB/.browse-modes-version" 2>/dev/null | head -1 || true
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

# ---- Docker helpers -----------------------------------------------------------

DK_WEB=""           # web root inside the container
DK_DATA="/config"   # Jellyfin data dir inside the container
DK_CFG_HOST=""      # host path of the data dir when it is a bind mount we can write
DK_WEB_RW=1
DK_DATA_RW=1
DK_NETMODE=""
DK_MOUNTS=""
DK_ROOTFS_RW=1
DK_PLUGINS="plugins"  # plugin dir relative to the data dir

dk_path_ok() { [[ "$1" =~ ^/[A-Za-z0-9/_.-]+$ ]]; }

# Whether path $1 inside the container is writable according to its mounts and
# a read-only root filesystem.
dk_rw_for() {
  local path="$1" best="" rw="$DK_ROOTFS_RW" kind dest src mrw
  while IFS=$'\t' read -r kind dest src mrw; do
    [ "$kind" = "mount" ] || continue
    if [ "$path" = "$dest" ] || [[ "$path" == "$dest"/* ]]; then
      if [ "${#dest}" -gt "${#best}" ]; then best="$dest"; rw="$mrw"; fi
    fi
  done <<< "$DK_MOUNTS"
  [ "$rw" = "1" ]
}

# Reads the container's settings. Returns 13 when the docker daemon is not
# usable, 10 when there is no such container, 11 when it is not running, 12 when
# it does not look like a Jellyfin image.
docker_prepare() {
  local insp facts key a b c w cand src
  docker info >/dev/null 2>&1 || return 13
  insp=$(docker inspect --type container "$CONTAINER" 2>/dev/null) || return 10
  facts=$(printf '%s' "$insp" | python3 -I -c '
import json, sys
d = json.load(sys.stdin)
c = d[0] if isinstance(d, list) and d else None
if not isinstance(c, dict):
    sys.exit(1)
st, cfg, hc = c.get("State") or {}, c.get("Config") or {}, c.get("HostConfig") or {}
env = {}
for e in cfg.get("Env") or []:
    k, _, v = e.partition("=")
    env[k] = v
def one(s):
    return str(s).replace("\t", " ").replace("\n", " ")
print("running\t" + ("1" if st.get("Running") else "0"))
print("rootfs_rw\t" + ("0" if hc.get("ReadonlyRootfs") else "1"))
print("webdir\t" + one(env.get("JELLYFIN_WEB_DIR", "")))
print("datadir\t" + one(env.get("JELLYFIN_DATA_DIR", "")))
print("netmode\t" + one(hc.get("NetworkMode", "")))
for m in c.get("Mounts") or []:
    print("mount\t%s\t%s\t%s" % (one(m.get("Destination", "")), one(m.get("Source", "")), "1" if m.get("RW") else "0"))
    print("kind\t%s\t%s" % (one(m.get("Destination", "")), one(m.get("Type", ""))))
') || return 10
  local running=0 envweb="" envdata=""
  DK_ROOTFS_RW=1; DK_MOUNTS=""; DK_NETMODE=""
  local kinds=""
  while IFS=$'\t' read -r key a b c; do
    case "$key" in
      running) running="$a" ;;
      rootfs_rw) DK_ROOTFS_RW="$a" ;;
      webdir) envweb="$a" ;;
      datadir) envdata="$a" ;;
      netmode) DK_NETMODE="$a" ;;
      mount) DK_MOUNTS="${DK_MOUNTS}mount"$'\t'"$a"$'\t'"$b"$'\t'"$c"$'\n' ;;
      kind) kinds="${kinds}${a}"$'\t'"${b}"$'\n' ;;
    esac
  done <<< "$facts"
  [ "$running" = "1" ] || return 11

  DK_DATA="/config"
  if dk_path_ok "$envdata"; then DK_DATA="$envdata"; fi
  # Some images (linuxserver) keep plugins one level down, in data/plugins.
  DK_PLUGINS="plugins"
  if ! docker exec "$CONTAINER" test -d "$DK_DATA/plugins" >/dev/null 2>&1 \
    && docker exec "$CONTAINER" test -d "$DK_DATA/data/plugins" >/dev/null 2>&1; then
    DK_PLUGINS="data/plugins"
  fi
  DK_WEB=""
  for cand in "$envweb" /jellyfin/jellyfin-web /usr/share/jellyfin/web /usr/lib/jellyfin/bin/jellyfin-web; do
    dk_path_ok "$cand" || continue
    if docker exec "$CONTAINER" test -f "$cand/index.html" >/dev/null 2>&1; then DK_WEB="$cand"; break; fi
  done
  [ -n "$DK_WEB" ] || return 12

  DK_WEB_RW=0; DK_DATA_RW=0
  if dk_rw_for "$DK_WEB"; then DK_WEB_RW=1; fi
  if dk_rw_for "$DK_DATA"; then DK_DATA_RW=1; fi

  # A bind-mounted data dir can be written straight from the host, which keeps
  # the files owned the way the existing plugin dirs are.
  DK_CFG_HOST=""
  src=""
  while IFS=$'\t' read -r key a b c; do
    [ "$key" = "mount" ] && [ "$a" = "$DK_DATA" ] || continue
    src="$b"
  done <<< "$DK_MOUNTS"
  w=$(printf '%s' "$kinds" | awk -F'\t' -v d="$DK_DATA" '$1 == d { print $2; exit }')
  if [ "$w" = "bind" ] && [ -n "$src" ] && [ -d "$src" ]; then
    if [ "$(id -u)" -eq 0 ]; then
      DK_CFG_HOST="$src"
    elif [ "$(stat -c %u "$src" 2>/dev/null)" = "$(id -u)" ] && [ -w "$src" ] \
      && [ -z "$(find "$src/$DK_PLUGINS" ! -writable -print -quit 2>/dev/null)" ]; then
      # Not root: only when nothing under plugins/ (often written by a root
      # container) is out of our reach; otherwise docker cp does the work.
      DK_CFG_HOST="$src"
    fi
  fi
  return 0
}

# Names of the installed Browse Modes plugin dirs (one per line).
dk_plugin_dirs() {
  if [ -n "$DK_CFG_HOST" ]; then
    ls -1 "$DK_CFG_HOST/$DK_PLUGINS" 2>/dev/null | grep '^Browse Modes_' || true
  else
    docker exec "$CONTAINER" ls -1 "$DK_DATA/$DK_PLUGINS" 2>/dev/null | grep '^Browse Modes_' || true
  fi
}

dk_plugin_meta() {
  if [ -n "$DK_CFG_HOST" ]; then
    cat "$DK_CFG_HOST/$DK_PLUGINS/$1/meta.json" 2>/dev/null
  else
    docker exec "$CONTAINER" cat "$DK_DATA/$DK_PLUGINS/$1/meta.json" 2>/dev/null
  fi
}

dkx() { docker exec -u 0 "$CONTAINER" "$@"; }

# Empties the web root without removing the dir itself (it may be a mount point).
dk_web_clear() {
  dkx find "$DK_WEB" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
}

dk_web_put() { # dk_web_put <host dir whose contents are copied>
  local owner
  owner=$(dkx stat -c '%u:%g' "$DK_WEB") || return 1
  docker cp "$1/." "${CONTAINER}:${DK_WEB}/" || return 1
  dkx chown -R "$owner" "$DK_WEB" || return 1
}

dk_plugins_remove() {
  local d
  while IFS= read -r d; do
    [ -n "$d" ] || continue
    if [ -n "$DK_CFG_HOST" ]; then rm -rf "${DK_CFG_HOST:?}/$DK_PLUGINS/$d" || return 1
    else dkx rm -rf "$DK_DATA/$DK_PLUGINS/$d" || return 1; fi
  done < <(dk_plugin_dirs)
}

dk_plugin_put() { # dk_plugin_put <host dir> <plugin dir name>
  local owner
  if [ -n "$DK_CFG_HOST" ]; then
    mkdir -p "$DK_CFG_HOST/$DK_PLUGINS/$2" || return 1
    cp -a "$1/." "$DK_CFG_HOST/$DK_PLUGINS/$2/" || return 1
    if [ "$(id -u)" -eq 0 ]; then
      owner=$(stat -c '%u:%g' "$DK_CFG_HOST/$DK_PLUGINS" 2>/dev/null || stat -c '%u:%g' "$DK_CFG_HOST")
      chown -R "$owner" "$DK_CFG_HOST/$DK_PLUGINS/$2" || return 1
    fi
  else
    owner=$(dkx stat -c '%u:%g' "$DK_DATA") || return 1
    dkx mkdir -p "$DK_DATA/$DK_PLUGINS/$2" || return 1
    docker cp "$1/." "${CONTAINER}:${DK_DATA}/$DK_PLUGINS/$2/" || return 1
    dkx chown -R "$owner" "$DK_DATA/$DK_PLUGINS/$2" || return 1
  fi
}

# Copies the Browse Modes plugin dirs out into host dir $1.
dk_plugin_backup() {
  local d
  mkdir -p "$1" || return 1
  while IFS= read -r d; do
    [ -n "$d" ] || continue
    if [ -n "$DK_CFG_HOST" ]; then cp -a "$DK_CFG_HOST/$DK_PLUGINS/$d" "$1/" || return 1
    else docker cp "${CONTAINER}:${DK_DATA}/$DK_PLUGINS/$d" "$1/" || return 1; fi
  done < <(dk_plugin_dirs)
}

# ---- status ------------------------------------------------------------------

# Crontab lines (stdin) selected by their marker, which ends the line. Native
# jobs end in "# browse-modes-auto-update", Docker jobs add " docker:<name>".
# mode: keep (print matching lines) or drop (print the others). With ALL=1
# every browse-modes marker matches.
cron_filter() { # cron_filter keep|drop MARKER
  awk -v mode="$1" -v m="$2" -v all="$ALL" '
    { hit = (length($0) >= length(m) && substr($0, length($0) - length(m) + 1) == m)
      if (all == 1 && $0 ~ /# browse-modes-auto-update( docker:[A-Za-z0-9_.-]+)?$/) hit = 1
      if ((mode == "drop") != hit) print }'
}

# This run's marker: per container with --docker, plain for native.
job_marker() {
  if [ -n "$CONTAINER" ]; then printf '%s docker:%s' "$CRON_MARKER" "$CONTAINER"; else printf '%s' "$CRON_MARKER"; fi
}

cron_line() {
  command -v crontab >/dev/null 2>&1 || return 0
  crontab -l 2>/dev/null | cron_filter keep "$(job_marker)" | head -1 || true
}

# Describes why a container cannot be worked on (return codes of docker_prepare).
dk_problem() {
  case "$1" in
    13) printf 'cannot use the docker daemon (run as root or as a user in the docker group)' ;;
    10) printf 'container %s not found' "$CONTAINER" ;;
    11) printf 'container %s is not running' "$CONTAINER" ;;
    12) printf 'container %s does not look like a Jellyfin image (no jellyfin-web with an index.html found)' "$CONTAINER" ;;
  esac
}

do_status() {
  local rc=0 line ip iw others
  if [ -n "$CONTAINER" ]; then
    echo "Browse Modes updater status (Docker container ${CONTAINER})"
    command -v docker >/dev/null 2>&1 || { echo "docker not found" >&2; exit 1; }
    docker_prepare || rc=$?
    if [ "$rc" -ne 0 ]; then
      echo "  Container:            $(dk_problem "$rc")"
    fi
  else
    echo "Browse Modes updater status"
  fi
  if [ "$rc" -eq 0 ] && detect_server; then
    echo "  Jellyfin running:     ${version} (${base})"
  elif [ "$rc" -eq 0 ]; then
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
  others=""
  if command -v crontab >/dev/null 2>&1; then
    others=$(crontab -l 2>/dev/null | ALL=1 cron_filter keep x | cron_filter drop "$(job_marker)" || true)
  fi
  if [ -n "$others" ]; then
    echo "  Other browse-modes jobs in this crontab:"
    printf '%s\n' "$others" | sed 's/^/    cron: /'
  fi
  if [ -n "$(state_get last_run)" ]; then
    echo "  Last run:             $(state_get last_run) - $(state_get last_result): $(state_get last_message)"
  else
    echo "  Last run:             none recorded"
  fi
}

# ---- auto-update switch ------------------------------------------------------

do_enable() {
  local self src_sum min hour existing args new_line current others marker cron_path dockerbin rc=0
  command -v crontab >/dev/null 2>&1 || { echo "crontab not found; install cron first" >&2; exit 1; }
  for cmd in curl unzip python3 flock md5sum sha256sum; do
    command -v "$cmd" >/dev/null 2>&1 || echo "Warning: required command not found: $cmd (the updater will fail without it)" >&2
  done
  if [ -n "$CONTAINER" ]; then
    dockerbin=$(command -v docker) || { echo "docker not found (required by --docker)" >&2; exit 1; }
    docker_prepare || rc=$?
    case "$rc" in
      0) ;;
      11) echo "Note: $(dk_problem 11); the job does nothing until it is running." >&2 ;;
      *) echo "Cannot enable auto-update: $(dk_problem "$rc")" >&2; exit 1 ;;
    esac
  fi

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
  if [ -n "$CONTAINER" ]; then args="$args --docker $CONTAINER"; fi
  if [ "$HOST" != "127.0.0.1" ]; then args="$args --host $HOST"; fi
  if [ "$PORT" != "8096" ]; then args="$args --port $PORT"; fi

  # cron runs with a minimal PATH that often lacks docker's directory.
  cron_path=""
  if [ -n "$CONTAINER" ]; then
    cron_path="PATH=$(dirname "$dockerbin"):/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin "
    if [[ ! "$cron_path" =~ ^PATH=[A-Za-z0-9/_.:-]+\ $ ]]; then
      echo "Refusing to put an unusual docker path into a cron line: ${dockerbin}" >&2
      exit 1
    fi
  fi

  marker=$(job_marker)
  current=$(crontab -l 2>/dev/null || true)
  existing=$(printf '%s\n' "$current" | cron_filter keep "$marker" | head -1 || true)
  if [[ "$existing" =~ ^([0-9]+)[[:space:]]+([0-9]+)[[:space:]] ]]; then
    # Keep the schedule picked the first time so re-running does not churn it.
    min="${BASH_REMATCH[1]}"; hour="${BASH_REMATCH[2]}"
  else
    min=$((RANDOM % 60)); hour=$((2 + RANDOM % 4))
  fi
  new_line="$min $hour * * * ${cron_path}$LIB_DIR/updater.sh $args $marker"

  if [ "$(printf '%s\n' "$current" | cron_filter keep "$marker" | grep -c . || true)" = "1" ] && [ "$existing" = "$new_line" ]; then
    echo "Auto-update was already enabled; nothing added to the crontab."
  else
    others=$(printf '%s\n' "$current" | cron_filter drop "$marker" || true)
    if [ -n "$others" ]; then
      printf '%s\n%s\n' "$others" "$new_line" | crontab -
    else
      printf '%s\n' "$new_line" | crontab -
    fi
    echo "Added this line to the crontab of $(id -un):"
  fi
  echo "  $new_line"
  cat <<EOF

Installed local copy: ${LIB_DIR}/updater.sh (owned by $(id -un), sha256 ${src_sum})
It runs once a day, between 02:00 and 05:59, and only changes anything when
compat.json lists a tested release for your Jellyfin version. Results go to
${LOG_FILE} and syslog.
EOF
  if [ -n "$CONTAINER" ]; then
    cat <<EOF

If the container is recreated (image update, Watchtower) the web bundle is gone
until the next run puts it back, provided compat.json has an entry for the new
Jellyfin version. The plugin lives in the config volume and stays.
EOF
  fi
  cat <<EOF

To check on it:   bash ${LIB_DIR}/updater.sh --status${CONTAINER:+ --docker $CONTAINER}
To remove it:     bash ${LIB_DIR}/updater.sh --disable-auto-update${CONTAINER:+ --docker $CONTAINER}
Or by hand:       crontab -l | grep -vF '${marker}' | crontab -
EOF
}

do_disable() {
  local current others had marker remaining
  command -v crontab >/dev/null 2>&1 || { echo "crontab not found" >&2; exit 1; }
  marker=$(job_marker)
  current=$(crontab -l 2>/dev/null || true)
  had=$(printf '%s\n' "$current" | cron_filter keep "$marker" | grep -c . || true)
  others="$current"
  if [ "$had" != "0" ]; then
    others=$(printf '%s\n' "$current" | cron_filter drop "$marker" || true)
    if [ -n "$others" ]; then
      printf '%s\n' "$others" | crontab -
    else
      printf '' | crontab -
    fi
    if [ "$ALL" -eq 1 ]; then echo "Removed every browse-modes auto-update line from the crontab of $(id -un)."
    elif [ -n "$CONTAINER" ]; then echo "Removed the auto-update line for container ${CONTAINER} from the crontab of $(id -un)."
    else echo "Removed the auto-update line from the crontab of $(id -un)."; fi
  else
    echo "No matching auto-update line found in the crontab of $(id -un)."
  fi
  # The local copy is shared by all jobs; it goes with the last one.
  remaining=$(printf '%s\n' "$others" | ALL=1 cron_filter keep x | grep -c . || true)
  if [ "$remaining" != "0" ]; then
    echo "Kept ${LIB_DIR}/updater.sh: ${remaining} other browse-modes job(s) still use it."
  elif [ -e "$LIB_DIR/updater.sh" ]; then
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

# Runs the whole install flow for the current target. Always called in a
# subshell so its exits (and traps) end only that target.
install_target() {
if [ -z "$CONTAINER" ] && [ "$DRY_RUN" -eq 0 ] && [ "$(id -u)" -ne 0 ] && [ -z "${BM_SKIP_ROOT_CHECK:-}" ]; then
  err "This needs root for a native install; re-run with sudo"
  record_run failed "Needs root for a native install; re-run with sudo"
  exit 1
fi
for cmd in curl unzip python3 md5sum sha256sum; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Required command not found: $cmd" >&2; exit 1; }
done
if [ -n "$CONTAINER" ] && ! command -v docker >/dev/null 2>&1; then
  echo "docker not found (required by --docker)" >&2
  exit 1
fi

# One run at a time (cron vs. a manual run).
if [ "$DRY_RUN" -eq 0 ]; then
  mkdir -p "$STATE_DIR"
  command -v flock >/dev/null 2>&1 || { echo "Required command not found: flock" >&2; exit 1; }
  exec 9>"$STATE_DIR/lock${CONTAINER:+.docker-$CONTAINER}"
  if ! flock -n 9; then
    log "Another Browse Modes update is already running; exiting."
    set_result skipped "Another Browse Modes update is already running"
    exit 0
  fi
fi

backup=""
tmp=$(mktemp -d)
stopped=0

# Start the server again after we stopped it. Idempotent: only acts when
# $stopped is 1, so the EXIT trap can call it safely on every exit path.
start_server() {
  if [ "$stopped" -ne 1 ]; then
    return 0
  fi
  log "Starting Jellyfin via systemctl"
  systemctl start jellyfin
  stopped=0
}

stop_server() {
  log "Stopping Jellyfin via systemctl"
  systemctl stop jellyfin
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
    if jf_info 2>/dev/null | grep -q '"Version"'; then
      return 0
    fi
    sleep "$HEALTH_INTERVAL"
  done
  return 1
}

# ---- decide what applies ----
if [ -n "$CONTAINER" ]; then
  rc=0; docker_prepare || rc=$?
  case "$rc" in
    0) ;;
    10|11)
      msg="$(dk_problem "$rc"); nothing changed"
      if [ "$AUTO" -eq 1 ]; then
        log "$msg"
        record_run skipped "$msg"
        exit 0
      fi
      err "$msg"
      exit 1 ;;
    *) die "$(dk_problem "$rc")" ;;
  esac
fi
rc=0; detect_server || rc=$?
case "$rc" in
  0) ;;
  1) die "Could not reach Jellyfin server at ${base}" ;;
  *) die "Could not determine the Jellyfin version from ${base} (/System/Info/Public)" ;;
esac
log "Detected Jellyfin ${version}"
prev_plugin=$(installed_plugin_version || true)
prev_web=$(installed_web_version || true)

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

# Docker: a read-only mount or root filesystem cannot be changed by copying.
ro_skipped=0
if [ -n "$CONTAINER" ]; then
  if [ "$web_update" -eq 1 ] && [ "$DK_WEB_RW" -eq 0 ]; then
    log "The web root ${DK_WEB} in ${CONTAINER} is read-only (read-only mount or root filesystem); leaving the web bundle alone."
    web_update=0; ro_skipped=1
  fi
  if [ "$plugin_update" -eq 1 ] && [ "$DK_DATA_RW" -eq 0 ]; then
    log "The data dir ${DK_DATA} in ${CONTAINER} is read-only; leaving the plugin alone."
    plugin_update=0; ro_skipped=1
  fi
fi

if [ "$plugin_update" -eq 0 ] && [ "$web_update" -eq 0 ]; then
  if [ "$ro_skipped" -eq 1 ]; then
    log "Nothing to install: the parts that need updating are read-only in the container."
    record_run skipped "Read-only container paths; nothing changed (Jellyfin ${version})"
  else
    log "Already up to date; nothing to install."
    record_run noop "Already up to date (Jellyfin ${version})"
  fi
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
    dest_plugin="container ${CONTAINER}:${DK_DATA}/$DK_PLUGINS"; dest_web="container ${CONTAINER}:${DK_WEB}"
  else
    dest_plugin="$plugin_dir"; dest_web="$web_dir"
  fi
  if [ "$plugin_update" -eq 1 ]; then echo "  plugin ${C_PLUGIN} (md5 ok) into ${dest_plugin}/${pdir}"; fi
  if [ "$web_update" -eq 1 ]; then echo "  web bundle ${C_WEB} (sha256 ok) into ${dest_web}"; fi
  echo "Nothing was changed."
  set_result dry-run "Verified downloads; would install$([ "$plugin_update" -eq 1 ] && printf ' plugin %s' "$C_PLUGIN")$([ "$web_update" -eq 1 ] && printf ' web %s' "$C_WEB") (nothing changed)"
  exit 0
fi

installed_msg() {
  local m="Installed"
  if [ "$plugin_update" -eq 1 ]; then m="$m plugin ${C_PLUGIN}"; fi
  if [ "$web_update" -eq 1 ]; then m="$m web ${C_WEB}"; fi
  printf '%s for Jellyfin %s' "$m" "$version"
}

# Keeps the newest KEEP_BACKUPS backup dirs under $backup_root.
prune_backups() {
  local d
  # shellcheck disable=SC2012
  { ls -1dt "$backup_root"/[0-9]* 2>/dev/null || true; } | tail -n +$((KEEP_BACKUPS + 1)) | while read -r d; do
    rm -rf "$d"
  done
}

# ---- Docker: back up, copy into the running container, restart, health-check,
# roll back on failure ----
if [ -n "$CONTAINER" ]; then
  backup_root="$STATE_DIR/backups/docker-$CONTAINER"
  backup="$backup_root/$(date +%Y%m%d%H%M%S)"

  apply_docker() {
    local d
    mkdir -p "$backup" || return 1
    if [ "$web_update" -eq 1 ]; then
      mkdir -p "$backup/web" || return 1
      docker cp "${CONTAINER}:${DK_WEB}/." "$backup/web" || return 1
    fi
    if [ "$plugin_update" -eq 1 ]; then
      dk_plugin_backup "$backup/plugin" || return 1
    fi

    if [ "$plugin_update" -eq 1 ]; then
      dk_plugins_remove || return 1
      dk_plugin_put "$tmp/plugin_stage" "$pdir" || return 1
      log "Installed plugin ${C_PLUGIN} to ${DK_DATA}/$DK_PLUGINS/${pdir} in container ${CONTAINER}$([ -n "$DK_CFG_HOST" ] && printf ' (via %s)' "$DK_CFG_HOST")"
    fi
    if [ "$web_update" -eq 1 ]; then
      dk_web_clear || return 1
      dk_web_put "$tmp/web_stage" || return 1
      log "Installed web bundle ${C_WEB} to ${DK_WEB} in container ${CONTAINER}"
    fi
  }

  # Puts back what the backup holds for the parts this run replaced.
  restore_docker() {
    local d
    if [ "$plugin_update" -eq 1 ]; then
      dk_plugins_remove || return 1
      for d in "$backup/plugin"/*; do
        [ -d "$d" ] || continue
        dk_plugin_put "$d" "$(basename "$d")" || return 1
      done
    fi
    if [ "$web_update" -eq 1 ]; then
      dk_web_clear || return 1
      dk_web_put "$backup/web" || return 1
    fi
  }

  restart_container() {
    log "Restarting container ${CONTAINER}"
    docker restart "$CONTAINER" >/dev/null
  }

  if ! apply_docker; then
    err "Applying the update failed; restoring the previous files from ${backup}"
    if restore_docker; then
      msg_tail="restored from"
    else
      msg_tail="restore from"
    fi
    die "Update failed (${msg_tail} the backup in ${backup}); the container was not restarted"
  fi

  if restart_container && health_check; then
    log "Jellyfin in ${CONTAINER} is healthy after the update."
    prune_backups
    msg=$(installed_msg)
    log "$msg"
    record_run ok "$msg"
    log "Done. (backup of the previous files: ${backup})"
    exit 0
  fi

  err "Jellyfin did not come back after the update; rolling back from ${backup}"
  restored=0
  if restore_docker; then restored=1; fi
  restart_container || true
  if [ "$restored" -eq 1 ] && health_check; then
    msg="Update failed health check; rolled back to the previous files (backup kept in ${backup})"
  else
    msg="Update failed health check and Jellyfin is not answering after the rollback; check 'docker logs ${CONTAINER}' (backup in ${backup})"
  fi
  err "$msg"
  record_run rolled-back "$msg"
  exit 1
fi

# ---- native install: back up, replace, health-check, roll back on failure ----
backup_root="$STATE_DIR/backups"
backup="$backup_root/$(date +%Y%m%d%H%M%S)"
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
  msg=$(installed_msg)
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
}

# ---- discovery and interactive selection --------------------------------------

# Questions are read from the terminal itself (fd 3), not from stdin, so they
# also work when the script is piped from curl. BM_TTY lets the tests supply a
# file of answers instead.
INTERACTIVE=0
init_tty() {
  if [ -n "${BM_TTY:-}" ]; then
    if [ -r "$BM_TTY" ]; then exec 3<"$BM_TTY"; INTERACTIVE=1; fi
    return 0
  fi
  if [ -t 1 ] && ( : </dev/tty ) 2>/dev/null; then exec 3</dev/tty; INTERACTIVE=1; fi
  return 0
}

ANSWER=""
# Prints a prompt and reads one lowercased, trimmed answer. Returns 1 at end of input.
ask() {
  printf '%s' "$1"
  if ! IFS= read -r ANSWER <&3; then echo; return 1; fi
  if [ -n "${BM_TTY:-}" ]; then echo "$ANSWER"; fi
  ANSWER=$(printf '%s' "$ANSWER" | tr 'A-Z' 'a-z' | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')
}

# confirm "question" y|n: the default is used for an empty answer; end of input means no.
confirm() {
  local def="$2" hint="[y/N]"
  if [ "$def" = "y" ]; then hint="[Y/n]"; fi
  while true; do
    ask "$1 $hint " || return 1
    case "${ANSWER:-$def}" in
      y|yes) return 0 ;;
      n|no) return 1 ;;
      *) echo "Please answer y or n." ;;
    esac
  done
}

# parse_selection <answer> <count>: fills SEL with " 1 3 " style space-padded
# indexes. Accepts numbers, comma lists and ranges (1, 1,3, 1-3, "1 3").
SEL=""
parse_selection() {
  local toks tok a b i
  local -a parts
  SEL=" "
  read -ra parts <<< "${1//,/ }"
  [ "${#parts[@]}" -gt 0 ] || return 1
  for tok in "${parts[@]}"; do
    if [[ "$tok" =~ ^[0-9]+$ ]]; then a=$((10#$tok)); b=$a
    elif [[ "$tok" =~ ^([0-9]+)-([0-9]+)$ ]]; then a=$((10#${BASH_REMATCH[1]})); b=$((10#${BASH_REMATCH[2]}))
    else return 1; fi
    if [ "$a" -lt 1 ] || [ "$b" -gt "$2" ] || [ "$a" -gt "$b" ]; then return 1; fi
    for ((i = a; i <= b; i++)); do
      case "$SEL" in *" $i "*) ;; *) SEL="${SEL}${i} " ;; esac
    done
  done
}

# Candidate records, one per server found. Fields, separated by "|":
# id kind name state version url supported compat installed note prev_plugin
# prev_web plan_plugin plan_web major_minor image
CAND=()

# Prints one candidate record for target $1 (image name $2 for Docker).
probe_target() {
  local t="$1" img="${2:-}" state="" ver="" url="" sup=0 compat="" inst="" note="" rc=0 name kind
  set_target "$t"
  C_PLUGIN=""; C_WEB=""; C_WEB_SHA=""
  if [ -n "$CONTAINER" ]; then
    kind="docker"; name="$CONTAINER"
    docker_prepare || rc=$?
    case "$rc" in
      0) state="running"; url=$(dk_published_base 2>/dev/null || true); url="${url:-no published port}" ;;
      11) state="stopped"; note="stopped: start the container to update it" ;;
      12) state="running"; note="no Jellyfin web files found in the container" ;;
      13) state="unknown"; note="$(dk_problem 13)" ;;
      *) state="unknown"; note="could not inspect the container" ;;
    esac
  else
    kind="native"; name="native install"
    url="$base"
    if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet jellyfin 2>/dev/null; then state="running"; else state="stopped"; fi
  fi
  if [ "$state" = "running" ] && { [ -z "$CONTAINER" ] || [ "$rc" -eq 0 ]; }; then
    if detect_server; then
      ver="$version"; state="running"
    else
      ver=""; note="Jellyfin did not answer at ${base}"
    fi
  elif [ -z "$CONTAINER" ] && [ "$state" = "stopped" ]; then
    note="stopped: start Jellyfin to update it"
  fi
  if [ -n "$ver" ]; then
    rc=0; load_compat || rc=$?
    case "$rc" in
      0) compat="tested release for ${major_minor}: plugin ${C_PLUGIN}, web ${C_WEB}"; sup=1 ;;
      3) compat="no tested release for ${major_minor} (nothing will be changed)" ;;
      *) compat="could not read compat.json" ;;
    esac
  else
    compat="unknown (version not available)"
  fi
  if [ -z "$CONTAINER" ] && [ "$sup" -eq 1 ] && [ "$(id -u)" -ne 0 ] && [ -z "${BM_SKIP_ROOT_CHECK:-}" ] && [ "$DRY_RUN" -eq 0 ]; then
    sup=0; note="needs root: re-run with sudo"
  fi
  local pp pw
  pp=$(installed_plugin_version || true); pw=$(installed_web_version || true)
  inst="plugin ${pp:-none}, web bundle ${pw:-none}"
  printf '%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n' \
    "$t" "$kind" "$name" "$state" "${ver:-unknown}" "$url" "$sup" "$compat" "$inst" "$note" \
    "$pp" "$pw" "$C_PLUGIN" "$C_WEB" "$major_minor" "$img"
}

native_present() {
  if command -v systemctl >/dev/null 2>&1 && systemctl cat jellyfin.service >/dev/null 2>&1; then return 0; fi
  if command -v dpkg-query >/dev/null 2>&1; then
    local pkg
    for pkg in jellyfin-server jellyfin; do
      if dpkg-query -W -f='${Status}' "$pkg" 2>/dev/null | grep -q 'install ok installed'; then return 0; fi
    done
  fi
  [ -d "$web_dir" ]
}

# Finds the native install and Docker containers that run Jellyfin. A container
# counts when its image name says Jellyfin or, if it is running, when it holds a
# Jellyfin web root. Podman is not searched.
discover() {
  local line name img st rec
  CAND=()
  if native_present; then
    CAND+=("$(probe_target native)")
  fi
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    while IFS='|' read -r name img st; do
      [ -n "$name" ] || continue
      if [[ ! "$name" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]]; then continue; fi
      if [[ "$img" =~ [Jj]ellyfin ]]; then
        :
      elif [ "$st" = "running" ] && { docker exec "$name" test -f /jellyfin/jellyfin-web/index.html \
        || docker exec "$name" test -f /usr/share/jellyfin/web/index.html \
        || docker exec "$name" test -f /usr/lib/jellyfin/bin/jellyfin-web/index.html; } >/dev/null 2>&1; then
        :
      else
        continue
      fi
      rec=$(probe_target "docker:${name}" "$img")
      CAND+=("$rec")
    done < <(docker ps -a --format '{{.Names}}|{{.Image}}|{{.State}}' 2>/dev/null || true)
  fi
}

# Field n (1-based) of a record.
rf() { printf '%s' "$1" | cut -d'|' -f"$2"; }

print_candidates() {
  local i=1 rec
  echo "Jellyfin servers found on this machine:"
  for rec in "${CAND[@]}"; do
    printf '  %d) %s%s\n' "$i" "$(rf "$rec" 2)" "$([ "$(rf "$rec" 2)" = docker ] && printf ': %s (image %s)' "$(rf "$rec" 3)" "$(rf "$rec" 16)")"
    printf '       Jellyfin %s, %s, %s\n' "$(rf "$rec" 5)" "$(rf "$rec" 4)" "$(rf "$rec" 6)"
    printf '       Compatibility: %s\n' "$(rf "$rec" 8)"
    printf '       Installed now: %s\n' "$(rf "$rec" 9)"
    if [ -n "$(rf "$rec" 10)" ]; then printf '       Note: %s\n' "$(rf "$rec" 10)"; fi
    i=$((i + 1))
  done
}

# What will happen to the record's target.
plan_line() {
  local rec="$1" id bdir
  id=$(rf "$rec" 1)
  bdir=$(set_target "$id"; if [ -n "$CONTAINER" ]; then printf '%s/backups/docker-%s' "$STATE_DIR" "$CONTAINER"; else printf '%s/backups' "$STATE_DIR"; fi)
  if [ "$(rf "$rec" 4)" != "running" ]; then
    echo "  - $id: skipped (not running); nothing changes"
  elif [ "$(rf "$rec" 7)" != "1" ]; then
    echo "  - $id: skipped ($(rf "$rec" 8)$([ -n "$(rf "$rec" 10)" ] && printf '; %s' "$(rf "$rec" 10)")); nothing changes"
  elif [ "$(rf "$rec" 11)" = "$(rf "$rec" 13)" ] && { [ "$(rf "$rec" 14)" = "-" ] || [ "$(rf "$rec" 12)" = "$(rf "$rec" 14)" ]; }; then
    echo "  - $id: already up to date (plugin $(rf "$rec" 13), web $(rf "$rec" 14)); nothing changes"
  else
    echo "  - $id: install plugin $(rf "$rec" 13) and web bundle $(rf "$rec" 14) for Jellyfin $(rf "$rec" 15);"
    echo "      the current files are backed up under ${bdir}/ first, then the server is restarted"
  fi
}

# Chooses the targets to work on and leaves them in RUN_TARGETS.
RUN_TARGETS=()
choose_targets() {
  local i rec n idx actionable
  discover
  n="${#CAND[@]}"
  if [ "$n" -eq 0 ]; then
    echo "No Jellyfin server found (looked for a native install and for Docker containers)." >&2
    echo "Name one yourself with --target native, --target docker:NAME, or --host and --port." >&2
    exit 1
  fi
  print_candidates
  echo
  RUN_TARGETS=()
  local supported=()
  i=1
  for rec in "${CAND[@]}"; do
    if [ "$(rf "$rec" 7)" = "1" ] && [ "$(rf "$rec" 4)" = "running" ]; then supported+=("$i"); fi
    i=$((i + 1))
  done

  if [ "$ALL_TARGETS" -eq 1 ] || { [ "$YES" -eq 1 ] && { [ "$n" -gt 1 ] || [ "$INTERACTIVE" -eq 0 ]; }; }; then
    if [ "${#supported[@]}" -eq 0 ]; then
      echo "None of these servers has a tested Browse Modes release, so nothing was changed."
      exit 0
    fi
    SEL=" ${supported[*]} "
  elif [ "$n" -eq 1 ]; then
    SEL=" 1 "
  else
    while true; do
      echo "Which servers should be updated? Enter numbers (1, 1,3, 1-3), a for all with a tested release, or q to quit."
      ask "> " || { echo "No answer given; quitting." >&2; exit 1; }
      case "$ANSWER" in
        q|quit) echo "Nothing was changed."; exit 0 ;;
        a|all)
          if [ "${#supported[@]}" -eq 0 ]; then echo "None of the servers has a tested release."; continue; fi
          SEL=" ${supported[*]} "; break ;;
      esac
      if parse_selection "$ANSWER" "$n"; then break; fi
      echo "Not a valid choice: '${ANSWER}'. There are ${n} servers."
    done
  fi

  for idx in $SEL; do
    RUN_TARGETS+=("$(rf "${CAND[$((idx - 1))]}" 1)")
  done
  echo "Plan:"
  for idx in $SEL; do plan_line "${CAND[$((idx - 1))]}"; done
  echo
  actionable=0
  for idx in $SEL; do
    rec="${CAND[$((idx - 1))]}"
    if [ "$(rf "$rec" 4)" = "running" ] && [ "$(rf "$rec" 7)" = "1" ]; then actionable=$((actionable + 1)); fi
  done
  if [ "$actionable" -eq 0 ]; then
    echo "None of the chosen servers can be updated right now, so nothing was changed."
    exit 0
  fi
  if [ "$DRY_RUN" -eq 1 ]; then
    echo "Dry run: the downloads will be checked, nothing will be changed."
  elif [ "$YES" -eq 0 ] && [ "$INTERACTIVE" -eq 1 ]; then
    if [ "$n" -eq 1 ]; then
      confirm "Go ahead with this server?" y || { echo "Nothing was changed."; exit 0; }
    else
      confirm "Go ahead with these ${#RUN_TARGETS[@]} server(s)?" y || { echo "Nothing was changed."; exit 0; }
    fi
  fi
}

# ---- run the targets and summarise --------------------------------------------

RES_DIR=""
RUN_RCS=()

res_get() { sed -n "s/^$2=//p" "$1" 2>/dev/null | head -1; }

run_targets() {
  local t idx=0 rc
  RUN_RCS=()
  for t in "${RUN_TARGETS[@]}"; do
    if [ "$AUTO" -eq 0 ] && [ "${#RUN_TARGETS[@]}" -gt 1 ]; then echo; echo "== ${t}"; fi
    rm -f "$RES_DIR/$idx"
    set +e
    (
      set -e
      RESULT_FILE="$RES_DIR/$idx"
      set_target "$t"
      install_target
    )
    rc=$?
    set -e
    RUN_RCS+=("$rc")
    idx=$((idx + 1))
  done
}

# Result text and whether it counts as a failure: label|failed(0/1).
result_label() { # result_label <result file> <rc>
  local f="$1" rc="$2" r m
  r=$(res_get "$f" result); m=$(res_get "$f" message)
  case "$r" in
    ok)
      if [ -z "$(res_get "$f" prev_plugin)$(res_get "$f" prev_web)" ]; then echo "installed|0"; else echo "updated|0"; fi ;;
    noop) echo "already up to date|0" ;;
    no-entry) echo "skipped: ${m}|0" ;;
    skipped) echo "skipped: ${m}|0" ;;
    dry-run) echo "dry run: ${m}|0" ;;
    rolled-back) echo "rolled back: ${m}|1" ;;
    failed) echo "failed: ${m}|1" ;;
    *) if [ "$rc" -eq 0 ]; then echo "done|0"; else echo "failed: see the messages above|1"; fi ;;
  esac
}

# After a successful interactive install, offers the daily automatic update per target.
offer_auto_update() {
  local idx=0 t lbl line shown=0
  for t in "${RUN_TARGETS[@]}"; do
    lbl=$(result_label "$RES_DIR/$idx" "${RUN_RCS[$idx]}")
    case "${lbl%|*}" in
      installed|updated|"already up to date")
        line=$(set_target "$t"; cron_line)
        if [ -z "$line" ]; then
          if [ "$shown" -eq 0 ]; then
            echo
            echo "Automatic updates (optional): a daily job re-checks each server and installs the tested"
            echo "release when one exists, for example after a Jellyfin upgrade. Nothing is enabled unless you say yes."
            shown=1
          fi
          if confirm "Enable daily automatic updates for ${t}?" n; then
            if ( set +e; set_target "$t"; do_enable ); then :; else echo "Could not enable automatic updates for ${t}; see above." >&2; fi
            ENABLED_NOW+=("$t")
          fi
        fi ;;
    esac
    idx=$((idx + 1))
  done
}
ENABLED_NOW=()

print_summary() {
  local idx=0 t f lbl failed=0 ok_changed=0 skipped=0 uptodate=0 line auto logs="" rcs
  echo
  echo "Summary"
  echo "======="
  for t in "${RUN_TARGETS[@]}"; do
    f="$RES_DIR/$idx"
    lbl=$(result_label "$f" "${RUN_RCS[$idx]}")
    case "${lbl%|*}" in
      installed|updated) ok_changed=$((ok_changed + 1)) ;;
      "already up to date") uptodate=$((uptodate + 1)) ;;
      skipped*|dry\ run*) skipped=$((skipped + 1)) ;;
    esac
    if [ "${lbl#*|}" = "1" ]; then failed=$((failed + 1)); fi
    line=$(set_target "$t"; cron_line)
    if [ -n "$line" ]; then auto="enabled"; else auto="not enabled"; fi
    echo "  ${t}"
    echo "      Jellyfin:     $(res_get "$f" jellyfin)"
    echo "      Result:       ${lbl%|*}"
    echo "      Plugin now:   $(res_get "$f" plugin | sed 's/^$/none/')    Web bundle now: $(res_get "$f" web | sed 's/^$/none/')"
    if [ -n "$(res_get "$f" backup)" ]; then echo "      Backup:       $(res_get "$f" backup)"; fi
    echo "      Auto-update:  ${auto}"
    case " $logs " in *" $(set_target "$t"; printf '%s' "$LOG_FILE") "*) ;; *) logs="$logs $(set_target "$t"; printf '%s' "$LOG_FILE")" ;; esac
    idx=$((idx + 1))
  done
  echo
  echo "  ${#RUN_TARGETS[@]} target(s): ${ok_changed} installed or updated, ${uptodate} already up to date, ${skipped} skipped, ${failed} failed."
  if [ "$DRY_RUN" -eq 0 ]; then echo "  Log:${logs}"; fi
  echo
  echo "Next steps"
  if [ "$ok_changed" -gt 0 ]; then
    echo "  - Reload the Jellyfin web page with a hard refresh (Ctrl+Shift+R) so the browser picks up the new files."
  fi
  if [ "$failed" -gt 0 ]; then
    echo "  - A failed target was rolled back or left as it was; read the messages above and the log."
  fi
  echo "  - To undo an install, copy the files from the backup folder shown above back (see the README, Troubleshooting)."
  echo "  - Automatic updates are off unless shown as enabled. Turn them on with --enable-auto-update"
  echo "    (add --docker NAME for a container) and off with --disable-auto-update (--all removes every job)."
  SUMMARY_FAILED="$failed"
}
SUMMARY_FAILED=0

# ---- main (install) -----------------------------------------------------------

if [ "$AUTO" -eq 0 ]; then init_tty; fi
if [ "${#TARGETS[@]}" -gt 0 ]; then
  RUN_TARGETS=("${TARGETS[@]}")
elif [ "$AUTO" -eq 1 ] || [ "$ADDR_SET" -eq 1 ]; then
  RUN_TARGETS=("native")
else
  if [ "$INTERACTIVE" -eq 1 ] || [ "$YES" -eq 1 ] || [ "$ALL_TARGETS" -eq 1 ]; then
    for cmd in curl python3; do
      command -v "$cmd" >/dev/null 2>&1 || { echo "Required command not found: $cmd" >&2; exit 1; }
    done
    choose_targets
  else
    echo "No terminal is available to ask questions, so only the native install is used." >&2
    echo "Use --target native, --target docker:NAME, --all or --yes to choose servers without a terminal." >&2
    RUN_TARGETS=("native")
  fi
fi

RES_DIR=$(mktemp -d)
trap 'rm -rf "$RES_DIR"' EXIT
run_targets

if [ "$AUTO" -eq 0 ]; then
  if [ "$INTERACTIVE" -eq 1 ] && [ "$YES" -eq 0 ] && [ "$DRY_RUN" -eq 0 ]; then offer_auto_update; fi
  print_summary
fi

# One target keeps its own exit code (3 means no tested release); with several,
# any failed target makes the whole run fail.
if [ "${#RUN_TARGETS[@]}" -eq 1 ]; then
  exit "${RUN_RCS[0]}"
fi
rc_total=0
idx=0
for t in "${RUN_TARGETS[@]}"; do
  lbl=$(result_label "$RES_DIR/$idx" "${RUN_RCS[$idx]}")
  if [ "${lbl#*|}" = "1" ]; then rc_total=1; fi
  idx=$((idx + 1))
done
exit "$rc_total"
