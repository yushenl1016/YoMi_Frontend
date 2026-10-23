-- Apply to the shared database before deploying either application.
-- Additive only: existing Google subjects and all member records are preserved.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.FrontendMember', N'U') IS NULL
    THROW 50000, 'FrontendMember table is missing.', 1;
IF COL_LENGTH(N'dbo.FrontendMember', N'EmailPasswordHash') IS NULL
    ALTER TABLE dbo.FrontendMember ADD EmailPasswordHash nvarchar(512) NULL;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261022000000_AddFrontendEmailPassword')
    INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20261022000000_AddFrontendEmailPassword', N'5.0.10');
COMMIT TRANSACTION;
