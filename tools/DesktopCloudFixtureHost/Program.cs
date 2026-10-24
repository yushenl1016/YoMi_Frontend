using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using YoMi_Admin.Library.Models;
using YoMi_Frontend.Controllers;
using YoMi_Frontend.Services;

var directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../.."));
var projectRoot=Path.GetFullPath(Path.Combine(directory,"../.."));
var fixturePath=Path.Combine(projectRoot,"fixture.json");
// A private test CA avoids runtime-specific treatment of self-signed CA:false development leaves.
// The temporary leaf uses the Windows user key provider for SChannel; disposal releases it. No trust settings change.
using var caKey=RSA.Create(2048);
var caRequest=new CertificateRequest("CN=YoMi Local Fixture CA",caKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));
caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey,false));
using var ca=caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(2));
using var leafKey=RSA.Create(2048);
var leafRequest=new CertificateRequest("CN=localhost",leafKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new Oid("1.3.6.1.5.5.7.3.1")},false));
var names=new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); leafRequest.CertificateExtensions.Add(names.Build());
using var publicLeaf=leafRequest.Create(ca,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));
using var signedLeaf=publicLeaf.CopyWithPrivateKey(leafKey);
var pfxPassword=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
var pfx=signedLeaf.Export(X509ContentType.Pfx,pfxPassword);
using var certificate=new X509Certificate2(pfx,pfxPassword,X509KeyStorageFlags.UserKeySet);
CryptographicOperations.ZeroMemory(pfx);
var certificatePath=Path.Combine(directory,"fixture-ca.pem");
await File.WriteAllTextAsync(certificatePath,"-----BEGIN CERTIFICATE-----\n"+Convert.ToBase64String(ca.Export(X509ContentType.Cert),Base64FormattingOptions.InsertLineBreaks)+"\n-----END CERTIFICATE-----\n");
var serverCertificateFingerprint=string.Join(":",certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b=>b.ToString("X2")));
const bool trusted=false;
var database="YoMiDesktopFixture_"+Guid.NewGuid().ToString("N");
const string master="Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;Encrypt=false;TrustServerCertificate=true";
using var admin=new SqlConnection(master); await admin.OpenAsync();
async Task Admin(string sql) { using var command=new SqlCommand(sql,admin); await command.ExecuteNonQueryAsync(); }
await Admin("CREATE DATABASE ["+database+"];");
WebApplication app=null;
try
{
    var connectionString=new SqlConnectionStringBuilder(master) { InitialCatalog=database }.ConnectionString;
    using(var connection=new SqlConnection(connectionString))
    {
        await connection.OpenAsync();
        async Task Execute(string sql) { using var command=new SqlCommand(sql,connection); await command.ExecuteNonQueryAsync(); }
        await Execute(@"CREATE TABLE dbo.FrontendMember(Id int NOT NULL PRIMARY KEY,Name nvarchar(64) NOT NULL,Email nvarchar(320) NOT NULL,PasswordHash nvarchar(255) NOT NULL,EmailPasswordHash nvarchar(max) NULL,HasLoggedInNewSite bit NOT NULL,Role nvarchar(32) NOT NULL,CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),LastSignedIn datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),Birthday datetime2 NULL);
CREATE TABLE dbo.FrontendSiteSetting(Id int IDENTITY PRIMARY KEY,[Key] nvarchar(64) NOT NULL UNIQUE,Value nvarchar(max) NOT NULL,Label nvarchar(128) NOT NULL,Description nvarchar(256) NULL,SortOrder int NOT NULL,UpdatedAt datetime2 NOT NULL);
CREATE TABLE dbo.YoMiDocuments(FrontendMemberId int NOT NULL,DocumentKind varchar(20) NOT NULL,Payload nvarchar(max) NOT NULL,Revision bigint NOT NULL DEFAULT 1,UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),PRIMARY KEY(FrontendMemberId,DocumentKind));
CREATE TABLE dbo.YoMiSessions(FrontendMemberId int NOT NULL,GoogleSubject nvarchar(255) NOT NULL,TokenHash binary(32) NOT NULL UNIQUE,ExpiresAtUtc datetime2 NOT NULL,RevokedAtUtc datetime2 NULL);
INSERT dbo.FrontendMember(Id,Name,Email,PasswordHash,HasLoggedInNewSite,Role) VALUES(1,N'Fixture Admin','admin@fixture.invalid','fixture-admin',1,'admin'),(2,N'Fixture User','user@fixture.invalid','fixture-user',1,'user');");
        var migration=Path.Combine(projectRoot,"tools/DesktopPetOperations.sql"); await Execute(await File.ReadAllTextAsync(migration));
        for(var member=1;member<=2;member++)
        {
            var profile=DesktopPetEconomy.NewProfile(); profile["profile"]["displayName"]=member==1?"Fixture Admin":"Fixture User"; profile["profile"]["customField"]="keep-existing-profile";
            profile["focus"]["coins"]=6543; profile["focus"]["activeSeconds"]=1790; profile["focus"]["completedSessions"]=99; profile["focus"]["keyCount"]=321; profile["focus"]["unclaimedRewards"]=2;
            profile["inventory"]=new JsonArray("neon-lamp","outfit-maid","face-happy","decor-plant","background-beach"); profile["equipped"]=new JsonArray("neon-lamp","outfit-maid","idle:face-happy","decor-plant","background-beach");
            profile["baseLayout"]=JsonNode.Parse("{\"version\":2,\"theme\":\"background-beach\",\"placements\":[{\"id\":\"decor-plant\",\"x\":0.12,\"y\":0.94,\"scale\":1,\"flip\":false,\"layer\":\"front\"}]}"); profile["games"]["bonus"]=4;            if(member==1)
            {
                profile["focus"]["activeSeconds"]=1798; profile["focus"]["completedSessions"]=0; profile["focus"]["unclaimedRewards"]=0;
                profile["inventory"]=new JsonArray("neon-lamp"); profile["equipped"]=new JsonArray("neon-lamp"); profile["baseLayout"]=JsonNode.Parse("{\"version\":2,\"theme\":\"yumi-esports\",\"placements\":[]}");
            }
            using var command=new SqlCommand("INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES(@Id,'profile',@Profile),(@Id,'music',@Music);",connection);
            command.Parameters.AddWithValue("@Id",member); command.Parameters.AddWithValue("@Profile",profile.ToJsonString()); command.Parameters.AddWithValue("@Music","{\"version\":1,\"favorites\":[{\"id\":\"dQw4w9WgXcQ\",\"source\":\"youtube\",\"title\":\"Fixture favorite\"}],\"playlists\":[{\"id\":\"fixture-list\",\"name\":\"Existing playlist\",\"tracks\":[]}],\"settings\":{\"volume\":0.4}}"); await command.ExecuteNonQueryAsync();
        }
    }
    var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=Array.Empty<string>(),ContentRootPath=directory,EnvironmentName="Development" });
    ((IConfigurationBuilder)builder.Configuration).Sources.Clear(); builder.Configuration.AddInMemoryCollection(new Dictionary<string,string> {{"DesktopPet:TrustServerCertificate","true"}});
    builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(options=>options.Listen(IPAddress.Loopback,0,listen=>listen.UseHttps(certificate)));
    builder.Services.AddDbContext<DBContext>(options=>options.UseSqlServer(connectionString));
    builder.Services.AddScoped<DesktopPetStore>(); builder.Services.AddScoped<DesktopPetOperations>(); builder.Services.AddScoped<DesktopAnnouncements>(); builder.Services.AddSingleton<DesktopRooms>();
    builder.Services.AddSingleton<DesktopGoogleVerifier>(); builder.Services.AddScoped<GoogleMemberLogin>();
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory,"fixture-keys"))).SetApplicationName("YoMiDesktopFixture");
    builder.Services.AddControllers().AddApplicationPart(typeof(DesktopEconomyController).Assembly).ConfigureApplicationPartManager(manager=>manager.FeatureProviders.Add(new FixtureControllers()));
    app=builder.Build(); app.UseWebSockets(); app.MapControllers();
    app.MapPost("/__fixture/stop",(IHostApplicationLifetime lifetime)=>{ lifetime.StopApplication(); return Results.Ok(new {ok=true}); });
    await app.StartAsync();
    var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
    var url=address.Replace("127.0.0.1","localhost");
    var members=new List<object>();
    using(var scope=app.Services.CreateScope())
    {
        var store=scope.ServiceProvider.GetRequiredService<DesktopPetStore>();
        for(var id=1;id<=2;id++)
        {
            var sub=id==1?"fixture-admin":"fixture-user"; var session=JsonSerializer.SerializeToNode(await store.CreateSession(id,sub));
            members.Add(new {memberId=id,sub,token=session["token"].GetValue<string>(),role=id==1?"admin":"user",email=id==1?"admin@fixture.invalid":"user@fixture.invalid",displayName=id==1?"Fixture Admin":"Fixture User"});
        }
    }
    var fixture=new {url,dbName=database,certificatePath,certificateThumbprint=certificate.Thumbprint,serverCertificateFingerprint,certificateTrusted=trusted,processId=Environment.ProcessId,members};
    await File.WriteAllTextAsync(fixturePath,JsonSerializer.Serialize(fixture,new JsonSerializerOptions {WriteIndented=true}));
    Console.WriteLine("Fixture ready: "+url+"; details saved to fixture.json (tokens are not printed). Certificate trusted="+trusted);
    if(args.Contains("--self-test"))
    {
        await FixtureSelfTest.Run(url,certificate,members.Select(x=>JsonSerializer.SerializeToNode(x)).ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory,"self-test-results.json"),JsonSerializer.Serialize(new {passed=true,checkedAt=DateTimeOffset.UtcNow,tests="HTTPS real controllers: existing documents, bootstrap, preferences, replay, legacy PUT blocked, music retained, admin create/update/delete and user forbidden; two authenticated WebSockets create/join/chat/seat"}));
        if(args.Contains("--exit-after-test")) await app.StopAsync(); else await app.WaitForShutdownAsync();
    }
    else await app.WaitForShutdownAsync();
}
finally
{
    if(app!=null) await app.DisposeAsync();
    SqlConnection.ClearAllPools();
    if(!database.StartsWith("YoMiDesktopFixture_",StringComparison.Ordinal) || database.Length!=51) throw new InvalidOperationException("Refusing unsafe database cleanup.");
    await Admin("ALTER DATABASE ["+database+"] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ["+database+"];");
    if(File.Exists(fixturePath))
    {
        var current=JsonNode.Parse(await File.ReadAllTextAsync(fixturePath));
        if(current?["dbName"]?.GetValue<string>()==database) { current["stopped"]=true; foreach(var member in current["members"].AsArray()) member.AsObject().Remove("token"); await File.WriteAllTextAsync(fixturePath,current.ToJsonString(new JsonSerializerOptions {WriteIndented=true})); }
    }
    Console.WriteLine("Disposable fixture database removed.");
}

sealed class FixtureControllers : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts,ControllerFeature feature)
    {
        var allowed=new[]{typeof(DesktopController),typeof(DesktopEconomyController),typeof(DesktopCommunityController)};
        foreach(var controller in feature.Controllers.ToArray()) if(!allowed.Contains(controller.AsType())) feature.Controllers.Remove(controller);
    }
}






