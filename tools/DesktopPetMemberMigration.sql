-- Caller owns a SERIALIZABLE transaction and has backed up the existing rows.
IF OBJECT_ID('dbo.FrontendMember','U') IS NULL THROW 51200,'FrontendMember is required.',1;
IF OBJECT_ID('dbo.YoMiUsers','U') IS NOT NULL
BEGIN
 EXEC(N'
 IF EXISTS(SELECT 1 FROM dbo.YoMiUsers u WHERE
   (SELECT COUNT(*) FROM dbo.FrontendMember m WHERE m.HasLoggedInNewSite=1 AND m.PasswordHash COLLATE Latin1_General_100_BIN2=u.GoogleSubject)<>1)
   THROW 51201,''Every desktop identity must match exactly one verified FrontendMember; no email-only binding.'',1;
 SELECT u.UserId,m.Id AS FrontendMemberId INTO #MemberMap FROM dbo.YoMiUsers u
 JOIN dbo.FrontendMember m ON m.HasLoggedInNewSite=1 AND m.PasswordHash COLLATE Latin1_General_100_BIN2=u.GoogleSubject;
 IF EXISTS(SELECT FrontendMemberId FROM #MemberMap GROUP BY FrontendMemberId HAVING COUNT(*)>1) THROW 51202,''Ambiguous member mapping.'',1;
 SELECT map.FrontendMemberId,d.DocumentKind,d.Revision,d.UpdatedAtUtc,DATALENGTH(d.Payload) AS Bytes,HASHBYTES(''SHA2_256'',CONVERT(varbinary(max),d.Payload)) AS Hash
 INTO #DocumentsBefore FROM dbo.YoMiDocuments d JOIN #MemberMap map ON map.UserId=d.UserId;
 SELECT s.SessionId,map.FrontendMemberId,s.TokenHash,s.CreatedAtUtc,s.ExpiresAtUtc,s.RevokedAtUtc
 INTO #SessionsBefore FROM dbo.YoMiSessions s JOIN #MemberMap map ON map.UserId=s.UserId;
 ALTER TABLE dbo.YoMiDocuments ADD FrontendMemberId int NULL;
 ALTER TABLE dbo.YoMiSessions ADD FrontendMemberId int NULL;
 ALTER TABLE dbo.YoMiSessions ADD GoogleSubject varchar(255) COLLATE Latin1_General_100_BIN2 NULL;
 EXEC(N''UPDATE d SET FrontendMemberId=m.FrontendMemberId FROM dbo.YoMiDocuments d JOIN #MemberMap m ON m.UserId=d.UserId;
 UPDATE s SET FrontendMemberId=m.FrontendMemberId,GoogleSubject=u.GoogleSubject FROM dbo.YoMiSessions s JOIN #MemberMap m ON m.UserId=s.UserId JOIN dbo.YoMiUsers u ON u.UserId=s.UserId;
 ALTER TABLE dbo.YoMiDocuments ALTER COLUMN FrontendMemberId int NOT NULL;
 ALTER TABLE dbo.YoMiSessions ALTER COLUMN FrontendMemberId int NOT NULL;
 ALTER TABLE dbo.YoMiSessions ALTER COLUMN GoogleSubject varchar(255) COLLATE Latin1_General_100_BIN2 NOT NULL;'');
 ALTER TABLE dbo.YoMiDocuments DROP CONSTRAINT FK_YoMiDocuments_User, PK_YoMiDocuments;
 ALTER TABLE dbo.YoMiSessions DROP CONSTRAINT FK_YoMiSessions_User;
 DROP INDEX IX_YoMiSessions_User ON dbo.YoMiSessions;
 ALTER TABLE dbo.YoMiDocuments DROP COLUMN UserId;
 ALTER TABLE dbo.YoMiSessions DROP COLUMN UserId;
 EXEC(N''ALTER TABLE dbo.YoMiDocuments ADD CONSTRAINT PK_YoMiDocuments PRIMARY KEY(FrontendMemberId,DocumentKind);
 ALTER TABLE dbo.YoMiDocuments ADD CONSTRAINT FK_YoMiDocuments_Member FOREIGN KEY(FrontendMemberId) REFERENCES dbo.FrontendMember(Id);
 ALTER TABLE dbo.YoMiSessions ADD CONSTRAINT FK_YoMiSessions_Member FOREIGN KEY(FrontendMemberId) REFERENCES dbo.FrontendMember(Id);
 CREATE INDEX IX_YoMiSessions_Member ON dbo.YoMiSessions(FrontendMemberId,ExpiresAtUtc);
 IF EXISTS(SELECT * FROM #DocumentsBefore EXCEPT SELECT FrontendMemberId,DocumentKind,Revision,UpdatedAtUtc,DATALENGTH(Payload),HASHBYTES(''''SHA2_256'''',CONVERT(varbinary(max),Payload)) FROM dbo.YoMiDocuments)
 OR (SELECT COUNT(*) FROM #DocumentsBefore)<>(SELECT COUNT(*) FROM dbo.YoMiDocuments) THROW 51203,''''Document preservation failed.'''',1;
 IF EXISTS(SELECT * FROM #SessionsBefore EXCEPT SELECT SessionId,FrontendMemberId,TokenHash,CreatedAtUtc,ExpiresAtUtc,RevokedAtUtc FROM dbo.YoMiSessions)
 OR (SELECT COUNT(*) FROM #SessionsBefore)<>(SELECT COUNT(*) FROM dbo.YoMiSessions) THROW 51204,''''Session preservation failed.'''',1;'');
 DROP TABLE dbo.YoMiUsers;
 ');
END;
