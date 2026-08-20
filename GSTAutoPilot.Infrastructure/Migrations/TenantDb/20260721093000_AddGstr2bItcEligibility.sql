-- AddGstr2bItcEligibility — run against EACH TENANT database.
--
-- Hand-authored equivalent of the EF migration of the same name (dotnet-ef is
-- not installed on the build machine). Idempotent: safe to re-run, and safe on
-- a database where `dotnet ef database update` already applied the migration.
--
-- Adds GSTR-2B per-invoice ITC availability so credit the portal marks
-- ineligible (PoS rule, section 16(4) time-bar, etc.) is not claimed:
--   IsItcEligible         bit NOT NULL, default 1 (existing rows -> eligible)
--   ItcIneligibleReason   nvarchar(200) NULL (the GSTR-2B `rsn` when ineligible)
-- NOT-NULL bit with a default is an online metadata-only add on SQL Server.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.GSTR2BRecords', 'IsItcEligible') IS NULL
    ALTER TABLE dbo.GSTR2BRecords
        ADD IsItcEligible bit NOT NULL
            CONSTRAINT DF_GSTR2BRecords_IsItcEligible DEFAULT 1;

IF COL_LENGTH('dbo.GSTR2BRecords', 'ItcIneligibleReason') IS NULL
    ALTER TABLE dbo.GSTR2BRecords ADD ItcIneligibleReason nvarchar(200) NULL;

-- Record the migration so a later `dotnet ef database update` does not try to
-- apply it again. Skipped if the history table does not exist.
IF OBJECT_ID('dbo.__EFMigrationsHistory', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
                   WHERE MigrationId = N'20260721093000_AddGstr2bItcEligibility')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20260721093000_AddGstr2bItcEligibility', N'10.0.7');
END

COMMIT TRANSACTION;
GO
