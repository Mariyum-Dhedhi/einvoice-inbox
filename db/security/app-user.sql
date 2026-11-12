/*
  Least-privilege login for the application.
  The app can read and write data and run stored procedures, but cannot change the schema:
  if the application is ever compromised, it cannot drop or alter tables.
  Schema changes run only from db-init with the sa account.

  The password comes from the EINVOICE_APP_PASSWORD environment variable (sqlcmd reads it as $(EINVOICE_APP_PASSWORD)).
*/
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'einvoice_app')
    CREATE LOGIN einvoice_app WITH PASSWORD = N'$(EINVOICE_APP_PASSWORD)', CHECK_POLICY = ON, DEFAULT_DATABASE = EInvoice;
ELSE
    ALTER LOGIN einvoice_app WITH PASSWORD = N'$(EINVOICE_APP_PASSWORD)';

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'einvoice_app')
    CREATE USER einvoice_app FOR LOGIN einvoice_app;

ALTER ROLE db_datareader ADD MEMBER einvoice_app;
ALTER ROLE db_datawriter ADD MEMBER einvoice_app;
GRANT EXECUTE ON SCHEMA::dbo TO einvoice_app;   -- stored procedures (package 6)

-- Explicit denials, so a later role change cannot quietly widen access.
DENY ALTER ON SCHEMA::dbo TO einvoice_app;
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION TO einvoice_app;
DENY INSERT, UPDATE, DELETE ON dbo.__EFMigrationsHistory TO einvoice_app;

PRINT 'App user einvoice_app ready (read/write data, no schema changes).';
