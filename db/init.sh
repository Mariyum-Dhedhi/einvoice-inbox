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

# Passwords go through environment variables, never on the command line (visible in process lists).
export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
# -I turns QUOTED_IDENTIFIER on (sqlcmd defaults to off); filtered indexes require it.
SQLCMD="/opt/mssql-tools18/bin/sqlcmd -S ${DB_HOST:-sqlserver} -U sa -C -b -I -f 65001"

echo "1/4 database"
$SQLCMD -Q "IF DB_ID(N'EInvoice') IS NULL CREATE DATABASE EInvoice;"

echo "2/4 migrations"
$SQLCMD -d EInvoice -i /db/migrations/schema.sql

echo "3/4 app user"
$SQLCMD -d EInvoice -i /db/security/app-user.sql

echo "4/4 seed data"
$SQLCMD -d EInvoice -i /db/seed/seed.sql

echo "Database ready."
