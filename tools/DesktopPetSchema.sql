-- Canonical schema. Caller owns transaction; run member migration first for old schemas.
IF OBJECT_ID('dbo.FrontendMember','U') IS NULL THROW 51200,'FrontendMember is required.',1;
IF OBJECT_ID('dbo.YoMiUsers','U') IS NOT NULL THROW 51210,'Run DesktopPetMemberMigration.sql first.',1;
IF OBJECT_ID(N'dbo.YoMiDocuments', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.YoMiDocuments (
        FrontendMemberId int NOT NULL,
        DocumentKind varchar(16) NOT NULL,
        Payload nvarchar(max) NOT NULL,
        Revision bigint NOT NULL CONSTRAINT DF_YoMiDocuments_Revision DEFAULT (1),
        UpdatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_YoMiDocuments_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_YoMiDocuments PRIMARY KEY (FrontendMemberId, DocumentKind),
        CONSTRAINT FK_YoMiDocuments_Member FOREIGN KEY (FrontendMemberId) REFERENCES dbo.FrontendMember(Id),
        CONSTRAINT CK_YoMiDocuments_Kind CHECK (DocumentKind IN ('profile', 'music')),
        CONSTRAINT CK_YoMiDocuments_JSON CHECK (ISJSON(Payload) = 1),
        CONSTRAINT CK_YoMiDocuments_Revision CHECK (Revision > 0)
    );
END;

IF OBJECT_ID(N'dbo.YoMiSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.YoMiSessions (
        SessionId uniqueidentifier NOT NULL CONSTRAINT DF_YoMiSessions_Id DEFAULT NEWSEQUENTIALID(),
        FrontendMemberId int NOT NULL,
        GoogleSubject varchar(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
        TokenHash binary(32) NOT NULL,
        CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_YoMiSessions_Created DEFAULT SYSUTCDATETIME(),
        ExpiresAtUtc datetime2(3) NOT NULL,
        RevokedAtUtc datetime2(3) NULL,
        CONSTRAINT PK_YoMiSessions PRIMARY KEY (SessionId),
        CONSTRAINT FK_YoMiSessions_Member FOREIGN KEY (FrontendMemberId) REFERENCES dbo.FrontendMember(Id),
        CONSTRAINT UQ_YoMiSessions_TokenHash UNIQUE (TokenHash),
        CONSTRAINT CK_YoMiSessions_Expiry CHECK (ExpiresAtUtc > CreatedAtUtc)
    );
    CREATE INDEX IX_YoMiSessions_Member ON dbo.YoMiSessions(FrontendMemberId, ExpiresAtUtc);
END;

