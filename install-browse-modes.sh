#!/usr/bin/env bash
# Installs/updates the Browse Modes plugin and web bundle on a Jellyfin server.
# Detects the running server version (12.x only) and picks the matching assets
# from the latest GitHub releases.
#
# Ordering: the Jellyfin process is stopped before any plugin/web file is
# removed or replaced and started again afterwards. A running server holds the
# plugin DLLs open (especially on FUSE-backed config volumes under Docker),
# which makes "rm -rf" of the old plugin dir fail with "Directory not empty".
set -euo pipefail

PLUGIN_REPO="AvonWilliams/jellyfin-browse-modes"
WEB_REPO="AvonWilliams/jellyfin-web"
API="https://api.github.com/repos"

HOST="${JELLYFIN_HOST:-127.0.0.1}"
PORT="${JELLYFIN_PORT:-8096}"
CONTAINER=""
WEB_ONLY=0
PLUGIN_ONLY=0

plugin_dir="/var/lib/jellyfin/plugins"
web_dir="/usr/share/jellyfin/web"

usage() {
  cat <<'EOF'
Usage: install-browse-modes.sh [options]

Installs the Browse Modes plugin and web bundle on a Jellyfin server, using the
server's public info endpoint to detect the version (Jellyfin 12.x only).

Options:
  --host HOST          Jellyfin server host (default 127.0.0.1)
  --port PORT          Jellyfin server port (default 8096)
  --docker CONTAINER   Install into Docker container CONTAINER instead of a
                       native install (default: native via systemctl)
  --plugin-only        Install only the plugin, not the web bundle
  --web-only           Install only the web bundle, not the plugin
  -h, --help           Show this help

Environment: JELLYFIN_HOST and JELLYFIN_PORT set the defaults for --host/--port.
Native mode needs root (write access to /var/lib/jellyfin and /usr/share/jellyfin);
Docker mode needs the docker CLI and a container using the standard /config and
/jellyfin layout.
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
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ "$WEB_ONLY" -eq 1 ] && [ "$PLUGIN_ONLY" -eq 1 ]; then
  echo "--web-only and --plugin-only are mutually exclusive" >&2
  exit 2
fi

# Native mode writes to /var/lib/jellyfin and /usr/share/jellyfin, which need root.
if [ -z "$CONTAINER" ] && [ "$(id -u)" -ne 0 ]; then
  echo "Native mode needs root; re-run with sudo" >&2
  exit 1
fi

for cmd in curl unzip; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Required command not found: $cmd" >&2; exit 1; }
done
if [ -n "$CONTAINER" ] && ! command -v docker >/dev/null 2>&1; then
  echo "docker not found (required by --docker)" >&2
  exit 1
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
    echo "Starting container ${CONTAINER}"
    docker start "$CONTAINER"
  else
    echo "Starting Jellyfin via systemctl"
    systemctl start jellyfin
  fi
  stopped=0
}

cleanup() {
  # Safety net: never leave the server stopped if a later step failed.
  start_server || true
  rm -rf "$tmp"
}
trap cleanup EXIT

# ---- detect the running server version ----
base="http://${HOST}:${PORT}"
info=$(curl -fsSL --max-time 15 "${base}/System/Info/Public") || {
  echo "Could not reach Jellyfin server at ${base}" >&2
  exit 1
}
version=$(printf '%s' "$info" | grep -o '"Version"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 \
  | sed 's/.*"\([^"]*\)"$/\1/' || true)
if [ -z "$version" ]; then
  echo "Could not determine Jellyfin version from ${base}/System/Info/Public" >&2
  exit 1
fi

major_minor=$(printf '%s' "$version" | cut -d. -f1,2)
case "$major_minor" in
  12.2) abi="12.2.0" ;;
  12.1) abi="12.1.0" ;;
  12.0) abi="12.0.0" ;;
  *) echo "Unsupported Jellyfin version ${version}: Browse Modes supports 12.x only" >&2; exit 1 ;;
esac
echo "Detected Jellyfin ${version} (plugin targetAbi ${abi})"

# The browse-modes web fork is not yet compatible with Jellyfin 12.2 (dashboard
# crashes on the older bundle); install the plugin only unless the caller
# explicitly asked for web.
if [ "$major_minor" = "12.2" ] && [ "$WEB_ONLY" -eq 0 ]; then
  echo "Web bundle skipped: the browse-modes web fork does not support Jellyfin 12.2 yet"
  PLUGIN_ONLY=1
fi

# ---- decide what needs updating (no server files touched yet) ----
plugin_update=0
web_update=0

if [ "$WEB_ONLY" -eq 0 ]; then
  echo "Fetching latest Browse Modes plugin release..."
  release=$(curl -fsSL -H "Accept: application/vnd.github+json" "${API}/${PLUGIN_REPO}/releases/latest") || {
    echo "Failed to query the latest plugin release from GitHub" >&2
    exit 1
  }
  asset_url=$(printf '%s' "$release" | tr ',' '\n' | grep 'browser_download_url' \
    | grep -F "${abi}.zip" | sed 's/.*"browser_download_url"[^"]*"\([^"]*\)".*/\1/' | head -1 || true)
  if [ -z "$asset_url" ]; then
    echo "No plugin asset for targetAbi ${abi} in the latest release" >&2
    exit 1
  fi

  asset_name=$(basename "$asset_url")
  case "$asset_name" in
    "browse-modes_"*"_${abi}.zip") : ;;
    *) echo "Unexpected plugin asset name: ${asset_name}" >&2; exit 1 ;;
  esac
  # Latest release version, parsed from the asset filename browse-modes_<ver>_<abi>.zip
  latest_ver="${asset_name#browse-modes_}"
  latest_ver="${latest_ver%_${abi}.zip}"

  # Version currently installed (if any), from the existing plugin's meta.json.
  installed_ver=""
  if [ -n "$CONTAINER" ]; then
    meta=$(docker exec "$CONTAINER" sh -c 'cat /config/plugins/Browse\ Modes_*/meta.json 2>/dev/null' || true)
    installed_ver=$(printf '%s' "$meta" | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
  else
    for f in "$plugin_dir"/Browse\ Modes_*/meta.json; do
      [ -e "$f" ] || continue
      installed_ver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$f" | head -1)
      break
    done
  fi

  if [ -n "$installed_ver" ] && [ "$installed_ver" = "$latest_ver" ]; then
    echo "Plugin is already up to date (version ${latest_ver}); skipping plugin."
  else
    plugin_update=1
    echo "Downloading ${asset_name}"
    curl -fsSL -o "$tmp/plugin.zip" "$asset_url" || { echo "Download failed: ${asset_url}" >&2; exit 1; }

    # The zip contains the .dlls and meta.json at its top level (no wrapper
    # dir), so extract to a staging dir and place it into a <name>_<version>
    # folder.
    mkdir -p "$tmp/plugin_stage"
    unzip -q -o "$tmp/plugin.zip" -d "$tmp/plugin_stage"
    name=$(sed -n 's/.*"name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$tmp/plugin_stage/meta.json" | head -1)
    pver=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$tmp/plugin_stage/meta.json" | head -1)
    if [ -z "$name" ] || [ -z "$pver" ]; then
      echo "Plugin meta.json is missing name/version; cannot determine install directory" >&2
      exit 1
    fi
    pdir="${name}_${pver}"
  fi
fi

if [ "$PLUGIN_ONLY" -eq 0 ]; then
  # The web bundle has no in-file version marker, so there is nothing to
  # compare against: always download and apply the latest bundle.
  echo "Fetching latest Browse Modes web release..."
  wrel=$(curl -fsSL -H "Accept: application/vnd.github+json" "${API}/${WEB_REPO}/releases/latest") || {
    echo "Failed to query the latest web release from GitHub" >&2
    exit 1
  }
  web_url=$(printf '%s' "$wrel" | tr ',' '\n' | grep 'browser_download_url' \
    | grep -F 'jellyfin-web-browse-modes-' | sed 's/.*"browser_download_url"[^"]*"\([^"]*\)".*/\1/' | head -1 || true)
  if [ -z "$web_url" ]; then
    echo "No 12.x web bundle asset in the latest ${WEB_REPO} release" >&2
    exit 1
  fi

  echo "Downloading $(basename "$web_url")"
  curl -fsSL -o "$tmp/web.zip" "$web_url" || { echo "Download failed: ${web_url}" >&2; exit 1; }
  mkdir -p "$tmp/web_stage"
  unzip -q -o "$tmp/web.zip" -d "$tmp/web_stage"
  web_update=1
fi

if [ "$plugin_update" -eq 0 ] && [ "$web_update" -eq 0 ]; then
  echo "Already up to date; nothing to install."
  exit 0
fi

# ---- stop the server before touching any files ----
if [ -n "$CONTAINER" ]; then
  echo "Stopping container ${CONTAINER}"
  docker stop "$CONTAINER"
else
  echo "Stopping Jellyfin via systemctl"
  systemctl stop jellyfin
fi
stopped=1

# ---- install the plugin ----
if [ "$plugin_update" -eq 1 ]; then
  if [ -n "$CONTAINER" ]; then
    docker exec "$CONTAINER" mkdir -p /config/plugins
    # Plugin dir name contains a space; escape it for the container's sh.
    docker exec "$CONTAINER" sh -c 'rm -rf /config/plugins/Browse\ Modes_*'
    docker cp "$tmp/plugin_stage" "$CONTAINER:/config/plugins/${pdir}"
    # docker cp preserves the host uid, which may not match the container's jellyfin
    # user (uid 1000, no named user). Chown to the owner of /config so meta.json stays writable.
    jf_owner=$(docker exec "$CONTAINER" sh -c 'stat -c "%u:%g" /config')
    docker exec "$CONTAINER" chown -R "$jf_owner" "/config/plugins/${pdir}"
    echo "Installed plugin to container ${CONTAINER}:/config/plugins/${pdir}"
  else
    rm -rf "$plugin_dir/Browse Modes_"*
    mkdir -p "$plugin_dir/$pdir"
    cp -a "$tmp/plugin_stage/." "$plugin_dir/$pdir/"
    # Jellyfin runs as the jellyfin user and rewrites meta.json, so give it ownership.
    chown -R jellyfin:jellyfin "$plugin_dir/$pdir"
    echo "Installed plugin to ${plugin_dir}/${pdir}"
  fi
fi

# ---- install the web bundle ----
if [ "$web_update" -eq 1 ]; then
  ts=$(date +%Y%m%d%H%M%S)
  if [ -n "$CONTAINER" ]; then
    docker exec "$CONTAINER" sh -c "mkdir -p /jellyfin; if [ -d /jellyfin/jellyfin-web ]; then mv /jellyfin/jellyfin-web /jellyfin/jellyfin-web.bak.${ts}; fi"
    docker cp "$tmp/web_stage" "$CONTAINER:/jellyfin/jellyfin-web"
    # Same ownership fix as the plugin above.
    jf_owner=$(docker exec "$CONTAINER" sh -c 'stat -c "%u:%g" /config')
    docker exec "$CONTAINER" chown -R "$jf_owner" "/jellyfin/jellyfin-web"
    echo "Installed web bundle to container ${CONTAINER}:/jellyfin/jellyfin-web (backup: jellyfin-web.bak.${ts})"
  else
    if [ -d "$web_dir" ]; then mv "$web_dir" "${web_dir}.bak.${ts}"; fi
    mkdir -p "$web_dir"
    cp -a "$tmp/web_stage/." "$web_dir/"
    chown -R jellyfin:jellyfin "$web_dir"
    echo "Installed web bundle to ${web_dir} (backup: ${web_dir}.bak.${ts})"
  fi
fi

# ---- start the server back up ----
start_server

echo "Done."
