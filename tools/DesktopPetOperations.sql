-- Run once on the same database that already contains YoMiDocuments and FrontendMember.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.YoMiDesktopOperations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.YoMiDesktopOperations (
        FrontendMemberId int NOT NULL,
        OperationId uniqueidentifier NOT NULL,
        OperationType varchar(24) NOT NULL,
        RequestHash binary(32) NOT NULL,
        Receipt nvarchar(max) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        FocusStartedAtUtc datetime2(7) NULL,
        FocusEndedAtUtc datetime2(7) NULL,
        CompletedAtUtc datetime2(7) NULL,
        CONSTRAINT PK_YoMiDesktopOperations PRIMARY KEY (FrontendMemberId,OperationId),
        CONSTRAINT FK_YoMiDesktopOperations_Member FOREIGN KEY (FrontendMemberId) REFERENCES dbo.FrontendMember(Id),
        CONSTRAINT CK_YoMiDesktopOperations_Receipt CHECK (ISJSON(Receipt)=1)
    );
    CREATE INDEX IX_YoMiDesktopOperations_Focus ON dbo.YoMiDesktopOperations(FrontendMemberId,FocusEndedAtUtc) INCLUDE(FocusStartedAtUtc);
END;
COMMIT;
