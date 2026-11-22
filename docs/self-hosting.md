# The public demo, hosted on this PC

The live demo runs on this PC in Docker, including the language model on the GPU. Tailscale Funnel gives it a public
HTTPS address such as `https://einvoice.tail1234.ts.net`. Nothing is opened on the router, and the PC itself never
joins a Tailscale network: only a Tailscale **container** does, and the app shares that container's network.

The link works while the PC is on, with Docker running (and Ollama, for PDF invoices).

## What is reachable from the internet, and what is not

| Part | Reachable from outside | Notes |
|---|---|---|
| The app (`web`) | **Only through the tunnel** | Not published on the PC at all; it believes forwarded HTTPS and visitor addresses from the tunnel (loopback) only |
| SQL Server, KoSIT validator, mock ERP | No | Published on `127.0.0.1` only, as for development |
| Ollama | No | Bound to `127.0.0.1:11434`; the app reaches it through Docker's `host.docker.internal` |
| The PC | No | Not on a tailnet; no router port |

HTTPS ends **in the Tailscale container on this PC** (its certificate comes from Let's Encrypt). Tailscale's relays
forward encrypted traffic and cannot read it.

The app container runs as a non-root user with a read-only file system and no Linux capabilities. The tunnel container
also has no capabilities (it runs in userspace mode, without a network device).

The public demo mode (`Demo__Enabled=true`) adds:

- **Demo accounts:** one-click sign-in per role. Each click creates a new account with no password.
- **A banner on every page:** synthetic data, everything is reset every night, no real invoices.
- **Sign-in limit:** 20 sign-in attempts per visitor address and hour.
- **Upload limit:** 30 uploads per account and hour, which also limits how busy the GPU can get.

## Once: Tailscale

1. **Create an account.** Sign up at [tailscale.com](https://tailscale.com); the Personal plan is free.
2. **Turn on HTTPS.** In the admin console, under **DNS**, keep MagicDNS on and enable **HTTPS Certificates**.
3. **Check that Funnel is allowed.** Under **Access controls**, the policy needs a `funnel` attribute for the
   machine. New tailnets have it by default:
   ```json
   "nodeAttrs": [{ "target": ["autogroup:member"], "attr": ["funnel"] }]
   ```
4. **Create an auth key.** Under **Settings > Keys**, choose *Generate auth key*: not reusable and not ephemeral. Copy
   it into `.env` (never committed):
   ```
   TS_AUTHKEY=tskey-auth-...
   TS_HOSTNAME=einvoice
   ```
   The key is used once. The machine's identity is then kept in the `tailscale-state` volume.

   In the same file, set `ERP_CLIENT_SECRET` to a long random value (`openssl rand -hex 32`). The mock ERP issues
   tokens only to this client, and the app signs in with it. Without it, neither starts.
5. **Keep the address working.** After the first start, open **Machines** in the admin console, find `einvoice` and
   choose **Disable key expiry**. Otherwise the machine signs out after 180 days and the link stops working.

## Start, stop, update

```bash
docker compose --profile public up -d --build      # everything: database, validator, mock ERP, app, tunnel
docker exec einvoice-tailscale tailscale funnel status   # shows the public address
```

The first visit after a start takes a few seconds while the certificate is issued.

```bash
docker compose --profile public stop tailscale     # take the demo off the internet (the rest keeps running)
docker compose --profile public start tailscale    # and back on
docker compose --profile public down               # stop everything (data and keys are kept in their volumes)
git pull && docker compose --profile public up -d --build   # update to the latest main
```

## Optional: master data from the ERP

The ERP sync (`sync/erp-sync`) can fill the demo with the mock ERP's master data: 250 vendors, 10,000 purchase
orders and their goods receipts. It reads every 5 minutes and stays inside Docker's network: it talks to the mock ERP
and to the app through the tunnel container's network, never through the internet. To turn it on:
1. Put a key and its SHA-256 in `.env` (`key=$(openssl rand -hex 32); printf %s "$key" | sha256sum`), as
   `SYNC_API_KEY` and `SYNC_API_KEY_SHA256`.
2. Start it with the public stack:

```bash
docker compose --profile public --profile sync up -d --build
docker logs einvoice-erp-sync          # one "sync summary" line per entity and run
```

The first run loads everything and may take more than one run's four minutes. Each later run reads only what
changed. After the nightly reset, the next run loads everything again.

## Every night: back to the start

At 03:00 Berlin time (`RESET_AT` in `.env`), the `demo-reset` service drops the database and builds it again with
`db-init`'s own script (`db/demo/reset.sh`). Everything visitors did is gone: invoices, demo accounts, audit events,
bank account changes. The seeded vendors, orders and receipts are back, so the demo files in `demo/invoices` work
again. Open sessions end, because their accounts no longer exist.

The wait is checked against the clock every minute. If the PC was asleep at 03:00, the reset runs when it wakes.

```bash
docker logs einvoice-demo-reset                          # "Next reset: 2026-11-19 03:00 CET"
docker compose --profile public run --rm demo-reset now  # reset now, for example before showing the demo
```

**This is the same local `EInvoice` database that development uses.** While the public stack runs, your local
development data is reset every night too. It is synthetic data only. To keep it, stop the reset service:
`docker compose --profile public stop demo-reset`.

Without AI: set `AI_ENABLED=false` in `.env`. PDF invoices are then refused with a message, and XML and ZUGFeRD
invoices work as before.

Sign-in keys (for cookies and form tokens) are kept in the `web-keys` volume, so a restart or an update does not sign
anyone out. They are stored unencrypted on this PC; `docker compose --profile public down -v` deletes them, together
with everything else.

## Check it

`tests/public-stack/check.sh` talks to the app the way the tunnel does. It checks that:

- the page comes back in German
- it shows the demo banner and one button per role
- forwarded HTTPS is believed
- a demo visitor signs in through the database

CI runs it on every pull request (job `public-stack`, without a key, so no public address). To run it here:

```bash
docker run --rm --network container:einvoice-tailscale -v "$PWD/tests/public-stack:/check:ro" \
  --entrypoint sh einvoice-kosit-validator:1.6.3-xr-2026-08-31 /check/check.sh
```

(In Git Bash on Windows, put `MSYS_NO_PATHCONV=1` in front of the command and use `$(pwd -W)` instead of `$PWD`.)

## When something is wrong

| What you see | Why, and what to do |
|---|---|
| `docker logs einvoice-tailscale` shows a `login.tailscale.com` link | No auth key, or an old one. Set `TS_AUTHKEY` in `.env`, then `docker compose --profile public up -d` |
| `funnel status` says Funnel is not available | The `funnel` attribute is missing from the access policy (step 3), or HTTPS certificates are off (step 2) |
| The address answers, but PDF invoices are refused | Ollama is not running on the PC, or `AI_ENABLED=false` |
| `docker logs einvoice-demo-reset` shows "Reset failed" | The database could not be rebuilt (it may be missing until the next try). Run `docker compose --profile public run --rm demo-reset now` and read its output |
| Every request gets an error page | `docker logs einvoice-web`. An "antiforgery ... not an SSL request" error means the request did not come through the tunnel |
