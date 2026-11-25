#!/usr/bin/env bash
# The running public stack survives its tunnel: when the tailscale container is stopped and started, or dies and is
# restarted by Docker, it must reach the app again without anyone restarting the app. Asked from inside the tunnel's
# container, the way Funnel does it (127.0.0.1:8080, X-Forwarded-Proto: https). Works without an auth key too.
#   bash tests/public-stack/restart.sh
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Git Bash on Windows

TUNNEL=einvoice-tailscale
WEB=einvoice-web

# The login page from inside the tunnel, tried for a minute (without a key the tunnel itself restarts now and then).
reaches_app() {
  for _ in $(seq 1 30); do
    if docker exec "$TUNNEL" wget -q -T 5 -O - --header 'X-Forwarded-Proto: https' --header 'Accept-Language: de-DE' \
      http://127.0.0.1:8080/account/login 2>/dev/null | grep 'data-testid="demo-banner"' >/dev/null; then
      echo "ok: $1"
      return
    fi
    sleep 2
  done
  echo "FAILED: $1: the tunnel does not reach the app" >&2
  exit 1
}

started() { docker inspect "$1" --format '{{.State.StartedAt}}'; }

reaches_app "at the start"
web=$(started "$WEB")

docker stop -t 5 "$TUNNEL" >/dev/null
docker start "$TUNNEL" >/dev/null
reaches_app "after the tunnel was stopped and started"

before=$(started "$TUNNEL")
docker exec "$TUNNEL" kill 1
for _ in $(seq 1 30); do [ "$(started "$TUNNEL")" != "$before" ] && break; sleep 1; done
[ "$(started "$TUNNEL")" != "$before" ] || { echo "FAILED: Docker did not restart the dead tunnel" >&2; exit 1; }
reaches_app "after the tunnel died"

[ "$(started "$WEB")" = "$web" ] || { echo "FAILED: the app was restarted; it must keep running" >&2; exit 1; }
echo "Tunnel restarts OK."
