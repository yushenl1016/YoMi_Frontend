-- Apply to the shared database before deploying transfer screenshot support.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.FrontendTransferSupport', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FrontendTransferSupport (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_FrontendTransferSupport PRIMARY KEY,
        LegacyId int NOT NULL,
        Email nvarchar(320) NOT NULL,
        DisplayedName nvarchar(64) NULL,
        Screenshot varbinary(max) NOT NULL,
        CreatedAt datetime2 NOT NULL
    );
    CREATE INDEX IX_FrontendTransferSupport_CreatedAt ON dbo.FrontendTransferSupport (CreatedAt);
END;
IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20261023000000_AddFrontendTransferSupport')
    INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20261023000000_AddFrontendTransferSupport', N'5.0.10');
COMMIT TRANSACTION;
