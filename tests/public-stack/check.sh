#!/bin/sh
# Checks the running public stack the way the tunnel talks to the app: plain HTTP on 127.0.0.1:8080 inside the tunnel's
# network, with X-Forwarded-Proto: https and the visitor's address. Run in a container that shares that network:
#   docker run --rm --network container:einvoice-tailscale -v "$PWD/tests/public-stack:/check:ro" \
#     --entrypoint sh einvoice-kosit-validator:1.6.3-xr-2026-08-31 /check/check.sh
set -eu
cd /tmp
fail() { echo "FAILED: $1"; exit 1; }
APP=http://localhost:8080
TUNNEL='-H X-Forwarded-Proto:https -H X-Forwarded-For:203.0.113.10'

# Up yet? (The app starts after the database is ready.)
i=0
until curl -fsS -o login.html $TUNNEL -H 'Accept-Language: de-DE' -c jar.txt "$APP/account/login"; do
  i=$((i + 1)); [ "$i" -lt 60 ] || fail "the app did not answer (see docker logs einvoice-web)"; sleep 2
done

# German (ICU is in the image), the public demo's banner, one demo button per role.
grep -q 'data-testid="login-error"\|<h1 id="sign-in-title">' login.html || fail "no login page (an error page: did the request count as HTTPS? Tunnel__Enabled)"
grep -q '<html lang="de">' login.html || fail "not German (is ICU missing from the image?)"
grep -q 'Anmelden' login.html || fail "no German login text"
grep -q 'data-testid="demo-banner"' login.html || fail "no demo banner (Demo__Enabled)"
test "$(grep -o 'data-testid="demo-sign-in"' login.html | wc -l)" -eq 5 || fail "not one demo button per role"

# HTTPS as the tunnel says: HSTS is sent (it is sent on HTTPS only).
curl -fsS -o /dev/null -D headers.txt $TUNNEL "$APP/account/login"
grep -qi '^strict-transport-security:' headers.txt || fail "forwarded HTTPS not believed (no HSTS)"

# A demo visitor signs in through the database and lands on the inbox.
token=$(grep -o 'name="__RequestVerificationToken"[^>]*value="[^"]*"' login.html | head -1 | sed 's/.*value="//; s/"$//')
test -n "$token" || fail "no form token on the login page"
status=$(curl -sS -o /dev/null -w '%{http_code} %{redirect_url}' $TUNNEL -b jar.txt -c jar.txt \
  --data-urlencode "role=Clerk" --data-urlencode "__RequestVerificationToken=$token" "$APP/account/demo")
echo "demo sign-in: $status"
case "$status" in "302 "*) ;; *) fail "the demo sign-in did not redirect" ;; esac
inbox=$(curl -sS -o inbox.html -w '%{http_code}' $TUNNEL -b jar.txt "$APP/")
test "$inbox" = 200 || fail "the inbox answered $inbox"
grep -q 'data-testid="signed-in-user"[^>]*>demo-clerk-' inbox.html || fail "not signed in as a demo clerk"

echo "Public stack OK."
