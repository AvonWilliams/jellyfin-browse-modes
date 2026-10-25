#!/usr/bin/env bash
# Fake Jellyfin, stub docker/crontab and container helpers shared by the Docker
# and interactive tests. Sourced after tests/lib.sh; expects $work, $script, $repo.

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
        if ctr == "native":
            marker = os.path.join(work, "web", ".browse-modes-version")
            vfile = os.path.join(work, "jf_version")
        else:
            wp = os.path.join(work, "ctr", ctr, "webpath")
            webpath = open(wp).read().strip() if os.path.exists(wp) else "/jellyfin/jellyfin-web"
            marker = os.path.join(work, "ctr", ctr, "fs", webpath.lstrip("/"), ".browse-modes-version")
            vfile = os.path.join(work, "ctr", ctr, "version")
            if not os.path.exists(vfile):
                vfile = os.path.join(work, "jf_version")
        if os.path.exists(marker) and open(marker).read().strip() == "9.9.9":
            self.send_response(503); self.end_headers(); return
        v = open(vfile).read().strip()
        # Server Id: <work>/ctr/NAME/sid (or <work>/native_sid), else "id-NAME", so
        # different servers differ unless a test makes them the same server.
        sidf = os.path.join(work, "native_sid") if ctr == "native" else os.path.join(work, "ctr", ctr, "sid")
        sid = open(sidf).read().strip() if os.path.exists(sidf) else "id-" + ctr
        body = ('{"ServerName":"t","Version":"%s","Id":"%s"}' % (v, sid)).encode()
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
# fails), restart_fail (how many "docker restart" calls fail: a number or "all";
# a failing restart leaves the server down, a good one brings it back).
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
    [ -e "$W/jf_down" ] && exit 1
    echo "127.0.0.1:$(cat "$W/port")" ;;
  restart)
    if [ -s "$W/restart_fail" ]; then
      n=$(cat "$W/restart_fail")
      if [ "$n" != all ]; then
        n=$((n - 1))
        if [ "$n" -le 0 ]; then rm -f "$W/restart_fail"; else echo "$n" > "$W/restart_fail"; fi
      fi
      touch "$W/jf_down"; echo "Error response from daemon: cannot restart container" >&2; exit 1
    fi
    rm -f "$W/jf_down"
    exit 0 ;;
  ps)
    for d in "$W"/ctr/*/; do
      n="$(basename "$d")"
      img="jellyfin/jellyfin:12.3"; [ -f "$d/image" ] && img="$(cat "$d/image")"
      st="exited"; grep -q '"Running": true' "$d/inspect.json" && st="running"
      echo "$n|$img|$st"
    done ;;
  exec)
    while [ "${1:0:1}" = "-" ]; do
      case "$1" in -u) shift 2 ;; *) shift ;; esac
    done
    name="$1"; shift
    grep -q '"Running": true' "$W/ctr/$name/inspect.json" || { echo "Error: container $name is not running" >&2; exit 1; }
    fs="$W/ctr/$name/fs"
    case "$1" in
      curl|wget)
        if [ -e "$W/jf_down" ]; then exit 7; fi
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
mk_fs_stubs

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
    mounts.append({"Type": "bind", "Source": "/srv/web", "Destination": __import__("os").environ.get("WEBPATH") or "/jellyfin/jellyfin-web", "RW": False})
doc = [{"Name": "/" + name, "State": {"Running": running == "1"},
        "Config": {"Env": ["JELLYFIN_DATA_DIR=/config"] + (["JELLYFIN_WEB_DIR=/jellyfin/jellyfin-web"] if not __import__("os").environ.get("WEBPATH") else [])},
        "HostConfig": {"NetworkMode": "bridge", "ReadonlyRootfs": False}, "Mounts": mounts}]
json.dump(doc, open(w + "/ctr/" + name + "/inspect.json", "w"), indent=1)
' "$work" "$@"
}

# new_ctr <name> [bind|volume] [web_ro]: a fresh stock Jellyfin container with an
# old plugin installed. WEBPATH (default /jellyfin/jellyfin-web) and PLUGREL
# (default plugins) pick another image layout; IMAGE names the image.
new_ctr() {
  local n="$1" kind="${2:-bind}" web_ro="${3:-0}" cfg wp="${WEBPATH:-/jellyfin/jellyfin-web}" pr="${PLUGREL:-plugins}"
  rm -rf "${work:?}/ctr/$n" "${work:?}/vol/$n-config" "${work:?}/volumes/$n"
  mkdir -p "$work/ctr/$n/fs$wp"
  echo STOCK > "$work/ctr/$n/fs$wp/index.html"
  echo "$wp" > "$work/ctr/$n/webpath"
  echo "${IMAGE:-jellyfin/jellyfin:12.3}" > "$work/ctr/$n/image"
  if [ "$kind" = "bind" ]; then
    cfg="$work/vol/$n-config"; mkdir -p "$cfg"; ln -s "$cfg" "$work/ctr/$n/fs/config"
  else
    cfg="$work/ctr/$n/fs/config"; mkdir -p "$cfg"
  fi
  mkdir -p "$cfg/$pr/Browse Modes_2.2.0.0"
  echo '{"name":"Browse Modes","version":"2.2.0.0"}' > "$cfg/$pr/Browse Modes_2.2.0.0/meta.json"
  mk_inspect "$n" "$kind" 1 "$web_ro"
  echo "$n" > "$work/default_ctr"
}
cfgdir() { # cfgdir <name>: where the container's /config really lives
  if [ -d "$work/vol/$1-config" ]; then printf '%s' "$work/vol/$1-config"; else printf '%s' "$work/ctr/$1/fs/config"; fi
}
webdir() { printf '%s' "$work/ctr/$1/fs$(cat "$work/ctr/$1/webpath" 2>/dev/null || echo /jellyfin/jellyfin-web)"; }
restarts() { grep -c '^restart' "$work/docker.log" 2>/dev/null || true; }
reset_state() { rm -rf "${work:?}/state" "${work:?}/lib"; rm -f "$work/docker.log"; : > "$work/docker.log"; rm -f "$work"/{docker_denied,curl_missing,wget_missing,cp_fail,jf_down,restart_fail,rm_fail,mv_fail,rm.log}; }

# run_dk <args...>: runs the script against the scratch tree, output in $work/out.
run_dk() {
  PATH="$work/bin:$PATH" \
  BM_RAW_BASE="file://$work/rel/raw" BM_PLUGIN_REL_BASE="file://$work/rel/plugin" \
  BM_WEB_REL_BASE="file://$work/rel/web" BM_ALLOW_LOCAL_URLS=1 \
  BM_STATE_DIR="$work/state" BM_LIB_DIR="$work/lib" BM_SKIP_ROOT_CHECK=1 \
  BM_HEALTH_TRIES="${HT:-3}" BM_HEALTH_INTERVAL="${HI:-0}" \
    bash "$script" "$@" > "$work/out" 2>&1
}

state_file() { printf '%s' "$work/state/state.docker-$1"; }

