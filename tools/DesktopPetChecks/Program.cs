using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using YoMi_Frontend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using YoMi_Admin.Library.Models;

using var rsa = RSA.Create(2048);
var key = new RsaSecurityKey(rsa) { KeyId = "fixture" };
var audience = "fixture.apps.googleusercontent.com";
int checks = 0;
string Token(string aud = null, string issuer = "https://accounts.google.com", bool verified = true, bool expired = false) =>
    new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, aud ?? audience,
        new[] { new Claim("sub", "fixture-sub"), new Claim("email", "fixture@example.invalid"), new Claim("email_verified", verified ? "true" : "false", ClaimValueTypes.Boolean) },
        DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(expired ? -5 : 5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (Exception) { checks++; return; } throw new Exception("Accepted " + name); }
var keys = new[] { key };
Check(DesktopGoogleVerifier.ValidateWithKeys(Token(), keys, new[] { audience }).FindFirst("sub")?.Value == "fixture-sub", "Valid identity");
Reject(() => DesktopGoogleVerifier.ValidateWithKeys(Token("wrong-audience"), keys, new[] { audience }), "wrong audience");
Reject(() => DesktopGoogleVerifier.ValidateWithKeys(Token(issuer: "https://attacker.invalid"), keys, new[] { audience }), "wrong issuer");
Reject(() => DesktopGoogleVerifier.ValidateWithKeys(Token(verified: false), keys, new[] { audience }), "unverified email");
Reject(() => DesktopGoogleVerifier.ValidateWithKeys(Token(expired: true), keys, new[] { audience }), "expired token");
using var otherRsa = RSA.Create(2048);
Reject(() => DesktopGoogleVerifier.ValidateWithKeys(Token(), new[] { new RsaSecurityKey(otherRsa) }, new[] { audience }), "wrong signature");
Reject(() => DesktopGoogleVerifier.ValidateWithKeys(Token(), keys, Array.Empty<string>()), "empty audience configuration");
JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
DesktopPetDocuments.Validate("profile", Json("{\"profile\":{},\"focus\":{\"coins\":120,\"keyCount\":413},\"inventory\":[],\"equipped\":[],\"baseLayout\":{},\"games\":{}}")); checks++;
Reject(() => DesktopPetDocuments.Validate("profile", Json("[]")), "array document");
Reject(() => DesktopPetDocuments.Validate("profile", Json("{\"settings\":{\"serverUrl\":\"file:///secret\"}}")), "device fields");
Reject(() => DesktopPetDocuments.Validate("profile", Json("{\"focus\":{\"coins\":-1}}")), "negative progress");
Reject(() => DesktopPetDocuments.Validate("unknown", Json("{}")), "unknown document kind");
Reject(() => DesktopPetDocuments.Validate("music", Json("{\"favorites\":[{\"source\":\"local\",\"path\":\"C:/private.mp3\"}]}")), "local music path");
Console.WriteLine($"Desktop security/document checks: {checks} passed.");

if (args.Length == 3 && args[0] == "--sql")
{
    var config = new ConfigurationBuilder().AddJsonFile(args[1]).AddInMemoryCollection(new Dictionary<string,string> { ["DesktopPet:TrustServerCertificate"]="true" }).Build();
    await using var db = new DBContext(new DbContextOptionsBuilder<DBContext>().UseSqlServer(config.GetConnectionString("DefaultConnection")).Options);
    var store = new DesktopPetStore(db, config);
    using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.Required,
        new System.Transactions.TransactionOptions { IsolationLevel=System.Transactions.IsolationLevel.Serializable, Timeout=TimeSpan.FromSeconds(60) }, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
    {
        var connection = db.Database.GetDbConnection(); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandTimeout=45;
        command.CommandText = File.ReadAllText(args[2]); await command.ExecuteNonQueryAsync();
        string sub = "sql-check-" + Guid.NewGuid().ToString("N");
        command.CommandText = @"INSERT FrontendMember(Name,Email,PasswordHash,Role,CreatedAt,UpdatedAt,LastSignedIn,HasLoggedInNewSite)
VALUES('Desktop SQL check',@Email,@Sub,'user',SYSUTCDATETIME(),SYSUTCDATETIME(),SYSUTCDATETIME(),1); SELECT CAST(SCOPE_IDENTITY() AS int);";
        var p = command.CreateParameter(); p.ParameterName="@Email"; p.Value=sub+"@example.invalid"; command.Parameters.Add(p);
        p=command.CreateParameter(); p.ParameterName="@Sub"; p.Value=sub; command.Parameters.Add(p);
        var memberId = (int)await command.ExecuteScalarAsync();
        var session = JsonSerializer.SerializeToElement(await store.CreateSession(memberId,sub));
        var token = session.GetProperty("token").GetString();
        Check(await store.Member(token)==memberId,"Session owns its FrontendMember");
        Check(await store.Member(new string('f',64))==null,"Forged session rejected");
        var renewed = JsonSerializer.SerializeToElement(await store.RefreshSession(token));
        Check(renewed.GetProperty("memberId").GetInt32()==memberId && renewed.GetProperty("sub").GetString()==sub,"Renewal stays bound to the same member and Google identity");
        Check(renewed.GetProperty("expiresAt").GetDateTime().ToUniversalTime()>DateTime.UtcNow.AddDays(29),"Renewal extends server expiry by 30 days");
        Check(await store.Member(token)==memberId,"Existing token remains usable after renewal");
        Check(await store.RefreshSession(new string('f',64))==null && await store.RefreshSession("bad")==null,"Forged and malformed renewal rejected");
        await store.RefreshSession(token);
        command.Parameters.Clear(); command.CommandText="SELECT COUNT(*) FROM YoMiSessions WHERE FrontendMemberId=@Id;";
        p=command.CreateParameter();p.ParameterName="@Id";p.Value=memberId;command.Parameters.Add(p);
        Check((int)await command.ExecuteScalarAsync()==1,"Renewal reuses the existing session row");
        var expired = JsonSerializer.SerializeToElement(await store.CreateSession(memberId,sub));
        var expiredToken = expired.GetProperty("token").GetString();
        command.Parameters.Clear(); command.CommandText="UPDATE YoMiSessions SET CreatedAtUtc=DATEADD(day,-2,SYSUTCDATETIME()),ExpiresAtUtc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE TokenHash=@Hash;";
        p=command.CreateParameter();p.ParameterName="@Hash";p.Value=DesktopPetStore.TokenHash(expiredToken);command.Parameters.Add(p);await command.ExecuteNonQueryAsync();
        Check(await store.RefreshSession(expiredToken)==null && await store.Member(expiredToken)==null,"Server-expired sessions cannot be revived");
        Check(await store.Put(memberId,"profile",Json("{\"focus\":{\"coins\":120}}"),0)==1,"Create document revision");
        Check(await store.Put(memberId,"profile",Json("{\"focus\":{\"coins\":150}}"),1)==2,"Update revision");
        Check(await store.Put(memberId,"profile",Json("{\"focus\":{\"coins\":999}}"),1)==null,"Stale write rejected");
        var docs=JsonSerializer.SerializeToElement(await store.Documents(memberId));
        Check(docs.GetProperty("profile").GetProperty("payload").GetProperty("focus").GetProperty("coins").GetInt32()==150,"Stored JSON preserved after conflict");
        Check((await store.Documents(-1)).Count==0,"Other member has no document access");
        await store.Revoke(token); Check(await store.Member(token)==null,"Logout revokes session");
        Check(await store.RefreshSession(token)==null,"Revoked session cannot renew");
        var second = JsonSerializer.SerializeToElement(await store.CreateSession(memberId,sub));
        command.Parameters.Clear(); command.CommandText="UPDATE FrontendMember SET PasswordHash='different-google-sub' WHERE Id=@Id;";
        p=command.CreateParameter();p.ParameterName="@Id";p.Value=memberId;command.Parameters.Add(p);await command.ExecuteNonQueryAsync();
        Check(await store.Member(second.GetProperty("token").GetString())==null,"Changed Google binding invalidates session");
        Check(await store.RefreshSession(second.GetProperty("token").GetString())==null,"Changed Google binding prevents renewal");
        // No Complete(): fixtures, migration and all SQL writes roll back together.
    }
    Console.WriteLine($"Including real SQL transaction checks: {checks} passed; all changes rolled back.");
}
