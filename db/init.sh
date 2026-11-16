#!/bin/sh
# Runs once in the db-init container after SQL Server is healthy:
# create the database, apply migrations, create the least-privilege app user, load seed data.
# Every step is idempotent, so running it again is safe.
set -eu

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is not set}"
: "${EINVOICE_APP_PASSWORD:?EINVOICE_APP_PASSWORD is not set}"

# The app password is placed inside a SQL string literal; refuse characters that could break out of it.
case "$EINVOICE_APP_PASSWORD" in
  *\'*|*\"*|*\\*|*'$'*) echo "EINVOICE_APP_PASSWORD must not contain quotes, backslashes or dollar signs." >&2; exit 1 ;;
esac

# The database name: EInvoice, unless EINVOICE_DB names another one (the load test uses EInvoiceLoad, so a local
# load run never touches the development data). Letters and digits only: it is placed into SQL text.
DB="${EINVOICE_DB:-EInvoice}"
case "$DB" in
  ''|*[!A-Za-z0-9]*) echo "EINVOICE_DB must be letters and digits only." >&2; exit 1 ;;
esac

# Passwords go through environment variables, never on the command line (visible in process lists).
export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
# -I turns QUOTED_IDENTIFIER on (sqlcmd defaults to off); filtered indexes require it.
SQLCMD="/opt/mssql-tools18/bin/sqlcmd -S ${DB_HOST:-sqlserver} -U sa -C -b -I -f 65001"

echo "1/7 database"
$SQLCMD -Q "IF DB_ID(N'$DB') IS NULL CREATE DATABASE [$DB];"

echo "2/7 database settings"
$SQLCMD -d "$DB" -i /db/database.sql

echo "3/7 migrations"
$SQLCMD -d "$DB" -i /db/migrations/schema.sql

echo "4/7 stored procedures"
for procedure in /db/procedures/*.sql; do
  $SQLCMD -d "$DB" -i "$procedure"
done

echo "5/7 views"
for view in /db/views/*.sql; do
  $SQLCMD -d "$DB" -i "$view"
done

echo "6/7 app user"
$SQLCMD -d "$DB" -i /db/security/app-user.sql

echo "7/7 seed data"
$SQLCMD -d "$DB" -i /db/seed/seed.sql

echo "Database ready."
