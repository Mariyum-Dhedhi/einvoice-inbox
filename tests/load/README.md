# Load test

k6 drives the real app over HTTPS: clerks sign in through the login form, upload XRechnung invoices, open them,
re-match them and look at the exception queue. It runs against a separate database, `EInvoiceLoad`, filled with a
year of synthetic invoices; your development database is not touched. Results and method: `docs/performance.md`.

| File | What it is |
|---|---|
| `upload-and-match.js` | The k6 script (open model: `RATE` clerk turns per second for `DURATION`) |
| `invoice-template.xml` | The uploaded invoice with placeholders; generated and checked by `LoadTemplateTests` |
| `volume.sql` | 100,000 invoices from the past year, plus a busy order (PO-0101) with 5,000 earlier invoices |
| `state.sql` | Switches the load database between the states compared in `docs/performance.md` |
| `match-stats.sql` | SQL Server's own counters for `usp_MatchInvoice` during a run (pages and CPU per match) |
| `match-reads.sql` | Pages read by one match of a seeded invoice (STATISTICS IO) |
| `results/` | The measured runs: k6 summaries (`*.json`) and SQL Server counters (`*-match-stats.txt`) |

## Run

Docker running, `docker compose up -d` done, `.env` and the user secrets set as in `demo/README.md`. Git Bash:

```bash
export MSYS_NO_PATHCONV=1
# sqlcmd as sa inside the compose network; the password stays in the container's environment.
sql() { db=$1; shift; docker compose run --rm -v "$(pwd -W)/tests/load:/load:ro" --entrypoint /bin/sh db-init \
  -c "SQLCMDPASSWORD=\"\$MSSQL_SA_PASSWORD\" /opt/mssql-tools18/bin/sqlcmd -S sqlserver -U sa -C -b -I -f 65001 -d $db $*"; }

# 1. The load database: schema, procedures, views, app user, seed; then the volume (about 10 seconds).
docker compose run --rm -e EINVOICE_DB=EInvoiceLoad db-init
sql EInvoiceLoad -v Invoices=100000 BusyOrderInvoices=5000 -i /load/volume.sql

# 2. Optional: another state (before | po-index | after). Stop the app first.
sql EInvoiceLoad -v State=after -i /load/state.sql

# 3. The app on the load database: Release build, warnings only in the log, port 7270.
dotnet build src/EInvoice.Web -c Release
APP_PW=$(grep '^EINVOICE_APP_PASSWORD=' .env | cut -d= -f2-)
(cd src/EInvoice.Web && ConnectionStrings__EInvoice="Server=127.0.0.1,1433;Database=EInvoiceLoad;User Id=einvoice_app;Password=$APP_PW;Encrypt=True;TrustServerCertificate=True" \
  ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=https://localhost:7270 Logging__LogLevel__Default=Warning \
  ./bin/Release/net10.0/EInvoice.Web.exe) &

# 4. The load (k6 in the pinned image), with SQL Server's counters around it.
sql EInvoiceLoad -v Step=clear -i /load/match-stats.sql
export LOAD_PASSWORD=$(dotnet user-secrets list --project src/EInvoice.Web | grep '^DemoUsers:Password' | sed 's/^DemoUsers:Password = //')
docker run --rm --add-host=host.docker.internal:host-gateway \
  -v "$(pwd -W)/tests/load:/scripts:ro" -v "$(pwd -W)/tests/load/results:/results" \
  -e LOAD_PASSWORD -e RATE=10 -e DURATION=3m \
  grafana/k6:2.3.0@sha256:2dd4756ec6e14e62778916e1eb850108e4b499304c02df56760fa0bdb0fba0b8 \
  run --summary-export /results/<date>-<state>.json /scripts/upload-and-match.js
sql EInvoiceLoad -v Step=report -i /load/match-stats.sql
```

The app runs in Development because the demo accounts exist only there; the clerk is `clerk@example.com`. k6 accepts
the local development certificate without trusting it (`insecureSkipTLSVerify`), which is fine for a local run only.

Throw the load database away afterwards (it holds about 1 GB):

```bash
sql master -Q "\"ALTER DATABASE EInvoiceLoad SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE EInvoiceLoad;\""
```

## Rules

- Numbers in `docs/performance.md` and the README come only from runs saved in `results/`, quoted with machine,
  date, commit and state.
- A slow query found here gets a failing plan test first (`tests/EInvoice.IntegrationTests/Performance`), then the
  fix. A run is evidence for one machine, not a promise for another.
