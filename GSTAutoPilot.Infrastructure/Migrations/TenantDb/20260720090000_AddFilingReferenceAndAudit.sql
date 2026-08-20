-- AddFilingReferenceAndAudit — run against EACH TENANT database.
--
-- Hand-authored equivalent of the EF migration of the same name (dotnet-ef is
-- not installed on the build machine). Idempotent: safe to re-run, and safe on
-- a database where `dotnet ef database update` already applied the migration.
--
-- Adds the columns that make a GSTN filing traceable:
--   ReferenceId      correlates retsave -> retsubmit -> retfile
--   ErrorReportJson  GSTN's validation report from a rejected retsave
--   SubmittedOn      when the return was LOCKED on GSTN (not the same as filed)
--   FiledBy          which user filed it (audit)
--   Cin              challan id, when 3B tax was paid by challan
--
-- All columns are NULLable with no default, so this is an online metadata-only
-- change on SQL Server — no table rewrite, no downtime.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.Gstr1Filings', 'ReferenceId') IS NULL
    ALTER TABLE dbo.Gstr1Filings ADD ReferenceId nvarchar(100) NULL;

IF COL_LENGTH('dbo.Gstr1Filings', 'ErrorReportJson') IS NULL
    ALTER TABLE dbo.Gstr1Filings ADD ErrorReportJson nvarchar(max) NULL;

IF COL_LENGTH('dbo.Gstr1Filings', 'SubmittedOn') IS NULL
    ALTER TABLE dbo.Gstr1Filings ADD SubmittedOn datetime2 NULL;

IF COL_LENGTH('dbo.Gstr1Filings', 'FiledBy') IS NULL
    ALTER TABLE dbo.Gstr1Filings ADD FiledBy nvarchar(200) NULL;

IF COL_LENGTH('dbo.Gstr3bFilings', 'ReferenceId') IS NULL
    ALTER TABLE dbo.Gstr3bFilings ADD ReferenceId nvarchar(100) NULL;

IF COL_LENGTH('dbo.Gstr3bFilings', 'ErrorReportJson') IS NULL
    ALTER TABLE dbo.Gstr3bFilings ADD ErrorReportJson nvarchar(max) NULL;

IF COL_LENGTH('dbo.Gstr3bFilings', 'SubmittedOn') IS NULL
    ALTER TABLE dbo.Gstr3bFilings ADD SubmittedOn datetime2 NULL;

IF COL_LENGTH('dbo.Gstr3bFilings', 'FiledBy') IS NULL
    ALTER TABLE dbo.Gstr3bFilings ADD FiledBy nvarchar(200) NULL;

IF COL_LENGTH('dbo.Gstr3bFilings', 'Cin') IS NULL
    ALTER TABLE dbo.Gstr3bFilings ADD Cin nvarchar(50) NULL;

-- Record the migration so a later `dotnet ef database update` does not try to
-- apply it again. Skipped if the history table does not exist.
IF OBJECT_ID('dbo.__EFMigrationsHistory', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory
                   WHERE MigrationId = N'20260720090000_AddFilingReferenceAndAudit')
BEGIN
    INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20260720090000_AddFilingReferenceAndAudit', N'10.0.7');
END

COMMIT TRANSACTION;
GO
