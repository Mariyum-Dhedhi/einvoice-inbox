#!/bin/sh
# ZAP baseline scans of the running app: public pages, then signed in as a clerk.
# Fails (non-zero) on any alert not listed in tests/zap/rules.tsv. Reports go to $ZAP_REPORTS (default tests/zap/reports).
#
#   ZAP_PASSWORD=<the demo password> sh tests/zap/scan.sh https://localhost:7169 https://host.docker.internal:7169
#
# $1  where this script reaches the app (sign-in with curl)
# $2  where the ZAP container reaches the same app (default: $1; CI uses --network host, so they are the same)
# The app must run in Development with DemoUsers:Password set (the clerk account clerk@example.com exists only there).
set -eu

APP=${1:?app address, for example https://localhost:7169}
TARGET=${2:-$APP}
: "${ZAP_PASSWORD:?Set ZAP_PASSWORD to the demo accounts password.}"
USER_NAME=${ZAP_USER:-clerk@example.com}
HERE=$(cd "$(dirname "$0")" && pwd)
REPORTS=${ZAP_REPORTS:-$HERE/reports}
# ZAP 2.17.0 (stable build of 2026-10-26), pinned by digest.
IMAGE=zaproxy/zap-stable:20261026@sha256:030d3df57735dacbb83464854758e674dd9ad01d79d12a129a8e58b1fd384238
NETWORK=${ZAP_NETWORK:-}

mkdir -p "$REPORTS"
cp "$HERE/rules.tsv" "$REPORTS/rules.tsv"
chmod -R a+rwX "$REPORTS" # the container runs as its own user
JAR="$REPORTS/.session-cookies" # next to the reports: a path both Git Bash and native tools on Windows can use
trap 'rm -f "$JAR"' EXIT

# Sign in through the real login form (the development certificate is not trusted: -k, loopback only).
form=$(curl -sk -c "$JAR" -b "$JAR" "$APP/account/login")
token=$(printf '%s' "$form" | sed -n 's/.*name="__RequestVerificationToken" value="\([^"]*\)".*/\1/p' | head -1)
handler=$(printf '%s' "$form" | sed -n 's/.*name="_handler" value="\([^"]*\)".*/\1/p' | head -1)
[ -n "$token" ] && [ -n "$handler" ] || { echo "Login form not found at $APP/account/login" >&2; exit 1; }
curl -sk -c "$JAR" -b "$JAR" \
  --data-urlencode "Input.UserName=$USER_NAME" --data-urlencode "Input.Password=$ZAP_PASSWORD" \
  --data-urlencode "__RequestVerificationToken=$token" --data-urlencode "_handler=$handler" \
  "$APP/account/login" > /dev/null
session=$(awk '$6 == "__Host-einvoice" { print $7 }' "$JAR")
[ -n "$session" ] || { echo "Sign-in as $USER_NAME failed." >&2; exit 1; }

scan() { # $1 report name, rest: extra ZAP options
  name=$1; shift
  echo "== ZAP baseline: $name"
  # shellcheck disable=SC2086
  docker run --rm ${NETWORK:+--network "$NETWORK"} --add-host=host.docker.internal:host-gateway \
    -v "$REPORTS:/zap/wrk:rw" "$IMAGE" \
    zap-baseline.py -t "$TARGET/" -c rules.tsv -J "$name.json" -r "$name.html" -m 2 \
    -z "-config spider.postform=false $*"
}

status=0
scan public || status=$?
# Signed in: every request carries the clerk's session cookie. The spider never submits forms (postform=false), so it
# cannot sign itself out or change anything.
scan signed-in \
  -config replacer.full_list\(0\).description=session \
  -config replacer.full_list\(0\).enabled=true \
  -config replacer.full_list\(0\).matchtype=REQ_HEADER \
  -config replacer.full_list\(0\).matchstr=Cookie \
  -config replacer.full_list\(0\).regex=false \
  -config "replacer.full_list(0).replacement=__Host-einvoice=$session" || status=$?

echo "Reports: $REPORTS"
exit "$status"
