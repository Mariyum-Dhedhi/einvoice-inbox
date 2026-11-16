/*
  Database settings, applied right after the database is created (db-init and the test fixture run this script).

  READ_COMMITTED_SNAPSHOT: a read sees the last committed version of a row instead of waiting for a writer.
  The load test found the deadlock this removes: a match adding up the other invoices of an order waited for an
  upload's uncommitted invoice of that order, while the upload waited for the match (docs/performance.md,
  LockingTests). Writers still lock each other, and explicit locks keep working: the app locks of the match and
  the audit chain, and UPDLOCK/READPAST of the posting queue. Azure SQL Database has this on by default.

  Changing it needs the database to itself for a moment, so it is set only when it is off.
*/
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 1)
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;

SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();
