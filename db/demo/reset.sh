#!/bin/sh
# The public demo's reset (docs/self-hosting.md): drop the database and build it again with db-init's own script, so
# everything visitors did is gone and the seed is back, with no list of tables that a new table could be missing from.
# Open connections are ended (ROLLBACK IMMEDIATE): the app reconnects to the new database.
# Run by nightly.sh at 03:00 Berlin time, or at once: docker compose --profile public run --rm demo-reset now
set -eu

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is not set}"

# Letters and digits only, checked BEFORE anything is dropped: the name is placed into SQL text. (No default for an
# empty value: "EINVOICE_DB=" must not quietly mean the real database.)
DB="${EINVOICE_DB-EInvoice}"
case "$DB" in
  ''|*[!A-Za-z0-9]*) echo "EINVOICE_DB must be letters and digits only." >&2; exit 1 ;;
esac

export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
SQLCMD="/opt/mssql-tools18/bin/sqlcmd -S ${DB_HOST:-sqlserver} -U sa -C -b -I -f 65001"

echo "Reset: dropping $DB ($(date '+%F %H:%M %Z'))"
$SQLCMD -Q "IF DB_ID(N'$DB') IS NOT NULL BEGIN ALTER DATABASE [$DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$DB]; END"

EINVOICE_DB="$DB"
export EINVOICE_DB
exec /bin/sh /db/init.sh
