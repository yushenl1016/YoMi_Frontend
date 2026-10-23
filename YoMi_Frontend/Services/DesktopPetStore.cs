using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;

namespace YoMi_Frontend.Services
{
    public sealed class DesktopPetStore
    {
        private readonly DBContext _db;
        private readonly DbConnection _connection;
        public DesktopPetStore(DBContext db, IConfiguration config)
        {
            _db = db; _connection = db.Database.GetDbConnection();
            // This scoped context is shared with GoogleMemberLogin only for this desktop request.
            var builder = new SqlConnectionStringBuilder(_connection.ConnectionString) { Encrypt = true, TrustServerCertificate = config.GetValue<bool>("DesktopPet:TrustServerCertificate") };
            _connection.ConnectionString = builder.ConnectionString;
        }
        private async Task<DbCommand> Command(string sql, params (string Name, object Value)[] values)
        {
            if (_connection.State != ConnectionState.Open) await _connection.OpenAsync();
            var command = _connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 15;
            foreach (var value in values) { var p = command.CreateParameter(); p.ParameterName = value.Name; p.Value = value.Value; command.Parameters.Add(p); }
            return command;
        }
        public static byte[] TokenHash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
        public async Task<object> CreateSession(int memberId, string sub)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var expires = DateTime.UtcNow.AddDays(30);
            using var command = await Command(@"INSERT dbo.YoMiSessions(FrontendMemberId,GoogleSubject,TokenHash,ExpiresAtUtc)
SELECT Id,@Sub,@Hash,@Expires FROM dbo.FrontendMember WHERE Id=@Id AND HasLoggedInNewSite=1 AND PasswordHash COLLATE Latin1_General_100_BIN2=@Sub;",
                ("@Id", memberId), ("@Sub", sub), ("@Hash", TokenHash(token)), ("@Expires", expires));
            if (await command.ExecuteNonQueryAsync() != 1) throw new UnauthorizedAccessException();
            return new { token, expiresAt = expires, memberId, sub };
        }
        public async Task<object> RefreshSession(string token)
        {
            if (token == null || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{64}$")) return null;
            using var command = await Command(@"UPDATE s SET ExpiresAtUtc=DATEADD(day,30,SYSUTCDATETIME())
OUTPUT inserted.FrontendMemberId,inserted.GoogleSubject,inserted.ExpiresAtUtc
FROM dbo.YoMiSessions s JOIN dbo.FrontendMember m ON m.Id=s.FrontendMemberId
WHERE s.TokenHash=@Hash AND s.RevokedAtUtc IS NULL AND s.ExpiresAtUtc>SYSUTCDATETIME() AND m.HasLoggedInNewSite=1
AND m.PasswordHash COLLATE Latin1_General_100_BIN2=s.GoogleSubject;", ("@Hash", TokenHash(token)));
            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new { memberId = reader.GetInt32(0), sub = reader.GetString(1), expiresAt = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc) };
        }
        public async Task<int?> Member(string token)
        {
            if (token == null || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-f0-9]{64}$")) return null;
            using var command = await Command(@"SELECT m.Id FROM dbo.YoMiSessions s JOIN dbo.FrontendMember m ON m.Id=s.FrontendMemberId
WHERE s.TokenHash=@Hash AND s.RevokedAtUtc IS NULL AND s.ExpiresAtUtc>SYSUTCDATETIME() AND m.HasLoggedInNewSite=1
AND m.PasswordHash COLLATE Latin1_General_100_BIN2=s.GoogleSubject;", ("@Hash", TokenHash(token)));
            var id = await command.ExecuteScalarAsync(); return id == null || id is DBNull ? null : Convert.ToInt32(id);
        }
        public async Task<Dictionary<string, object>> Documents(int memberId)
        {
            using var command = await Command("SELECT DocumentKind,Payload,Revision,UpdatedAtUtc FROM dbo.YoMiDocuments WHERE FrontendMemberId=@Id;", ("@Id", memberId));
            using var reader = await command.ExecuteReaderAsync();
            var result = new Dictionary<string, object>();
            while (await reader.ReadAsync()) result[reader.GetString(0)] = new { payload = DesktopPetDocuments.Project(reader.GetString(0), reader.GetString(1)), revision = reader.GetInt64(2), updatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc) };
            return result;
        }
        public async Task<long?> Put(int memberId, string kind, JsonElement payload, long revision)
        {
            DesktopPetDocuments.Validate(kind, payload);
            if (revision < 0 || revision >= 9007199254740991) throw new ArgumentException("Invalid revision.");
            // One serializable transaction handles both creation and compare-and-swap.
            using var command = await Command(@"SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @Current bigint;
SELECT @Current=Revision FROM dbo.YoMiDocuments WITH(UPDLOCK,HOLDLOCK) WHERE FrontendMemberId=@Id AND DocumentKind=@Kind;
IF @Current IS NULL AND @Revision=0
BEGIN
 INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES(@Id,@Kind,@Payload); SELECT CAST(1 AS bigint);
END
ELSE IF @Current=@Revision
BEGIN
 UPDATE dbo.YoMiDocuments SET Payload=@Payload,Revision=Revision+1,UpdatedAtUtc=SYSUTCDATETIME() WHERE FrontendMemberId=@Id AND DocumentKind=@Kind;
 SELECT @Current+1;
END
ELSE SELECT CAST(NULL AS bigint);
COMMIT;", ("@Id", memberId), ("@Kind", kind), ("@Payload", payload.GetRawText()), ("@Revision", revision));
            var next = await command.ExecuteScalarAsync(); return next == null || next is DBNull ? null : Convert.ToInt64(next);
        }
        public async Task Revoke(string token)
        {
            using var command = await Command("UPDATE dbo.YoMiSessions SET RevokedAtUtc=SYSUTCDATETIME() WHERE TokenHash=@Hash AND RevokedAtUtc IS NULL;", ("@Hash", TokenHash(token)));
            await command.ExecuteNonQueryAsync();
        }
    }
}
