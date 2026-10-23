IF OBJECT_ID('dbo.YoMiUsers','U') IS NOT NULL THROW 51100, 'Duplicate desktop account table still exists.', 1;
IF COL_LENGTH('dbo.YoMiDocuments','FrontendMemberId') <> 4 OR COL_LENGTH('dbo.YoMiSessions','FrontendMemberId') <> 4
    THROW 51101, 'Desktop data must belong to FrontendMember.', 1;
IF COL_LENGTH('dbo.YoMiDocuments','UserId') IS NOT NULL OR COL_LENGTH('dbo.YoMiSessions','UserId') IS NOT NULL
    THROW 51102, 'Old identity columns remain.', 1;
IF (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id IN (OBJECT_ID('dbo.YoMiDocuments'),OBJECT_ID('dbo.YoMiSessions')) AND referenced_object_id=OBJECT_ID('dbo.FrontendMember')) <> 2
    THROW 51103, 'Member foreign keys are missing.', 1;

SAVE TRANSACTION DesktopMemberChecks;
SET XACT_ABORT OFF;
DECLARE @Email nvarchar(320)=CONVERT(varchar(36),NEWID())+'@example.invalid';
INSERT dbo.FrontendMember(Name,Email,PasswordHash,Role,CreatedAt,UpdatedAt,LastSignedIn,HasLoggedInNewSite)
VALUES(N'Schema check',@Email,'schema-check','user',SYSUTCDATETIME(),SYSUTCDATETIME(),SYSUTCDATETIME(),0);
DECLARE @Id int=SCOPE_IDENTITY();
INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES (@Id,'profile',N'{"focus":{"coins":321}}');
IF NOT EXISTS(SELECT 1 FROM dbo.YoMiDocuments WHERE FrontendMemberId=@Id AND Revision=1 AND JSON_VALUE(Payload,'$.focus.coins')='321') THROW 51104,'Document roundtrip failed.',1;
BEGIN TRY
 INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES (@Id,'profile',N'{}');
 THROW 51105,'Duplicate document accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN (2601,2627) THROW; END CATCH;
BEGIN TRY
 INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES (@Id,'music',N'bad-json');
 THROW 51106,'Invalid JSON accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>547 THROW; END CATCH;
BEGIN TRY
 INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES (-2147483648,'profile',N'{}');
 THROW 51107,'Orphan document accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>547 THROW; END CATCH;
DECLARE @Hash binary(32)=HASHBYTES('SHA2_256',CONVERT(varchar(36),NEWID()));
INSERT dbo.YoMiSessions(FrontendMemberId,GoogleSubject,TokenHash,ExpiresAtUtc) VALUES(@Id,'schema-check',@Hash,DATEADD(hour,1,SYSUTCDATETIME()));
BEGIN TRY
 INSERT dbo.YoMiSessions(FrontendMemberId,GoogleSubject,TokenHash,ExpiresAtUtc) VALUES(@Id,'schema-check',@Hash,DATEADD(hour,1,SYSUTCDATETIME()));
 THROW 51108,'Duplicate session accepted.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER() NOT IN(2601,2627) THROW; END CATCH;
ROLLBACK TRANSACTION DesktopMemberChecks;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.FrontendMember WHERE Id=@Id) THROW 51109,'Test member remained.',1;
