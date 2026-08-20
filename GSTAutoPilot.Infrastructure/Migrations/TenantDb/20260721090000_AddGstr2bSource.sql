-- AddGstr2bSource — run against EACH TENANT database.
--
-- Hand-authored equivalent of the EF migration of the same name (dotnet-ef is
-- not installed on the build machine). Idempotent: safe to re-run, and safe on
-- a database where `dotnet ef database update` already applied the migration.
--
-- Adds GSTR2BRecords.Source — provenance of the row ("GSTN" / "GSTN (N files)")
-- so a genuine GSTN pull is distinguishable from legacy rows (NULL Source, which
-- may be stale mock data from before the mock path was removed).
-- NULLable with no default => online metadata-only change, no table rewrite.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.GSTR2BRecords', 'Source') IS NULL
    ALTER TABLE dbo.GSTR2BRecords ADD Source nvarchar(40) NULL;

-- Record the migration so a later `dotnet ef database update` does not try to
-- apply it again. Skipped if the history table does not exist.
IF OBJECT_ID('dbo.__EFMigrationsHistory', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
                   WHERE MigrationId = N'20260721090000_AddGstr2bSource')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20260721090000_AddGstr2bSource', N'10.0.7');
END

COMMIT TRANSACTION;
GO
