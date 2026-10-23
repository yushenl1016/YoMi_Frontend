using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using YoMi_Frontend.Services;
using YoMi_Frontend.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text.Json;

var root=Path.Combine(Path.GetTempPath(),"yomi-api-checks-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int count=0;void Check(bool value,string label){if(!value)throw new Exception(label);count++;}
try {
    var folder=Path.Combine(root,"App_Data","DesktopPetReleases");Directory.CreateDirectory(folder);
    var file="YoMi_Desktop_Pet-0.7.3-win-x64.zip"; var bytes=new byte[2048];
    File.WriteAllBytes(Path.Combine(folder,file),bytes);
    var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    File.WriteAllText(Path.Combine(folder,"release.json"),JsonSerializer.Serialize(new {version="0.7.3",fileName=file,sizeBytes=bytes.Length,sha256=hash,releasedAt=DateTime.UtcNow,backendOrigin="https://localhost:7443"}));
    var controller=new DesktopDownloadController(new DesktopPetReleaseService(root));
    var metadata=JsonSerializer.Serialize(((OkObjectResult)controller.Latest()).Value);
    Check(metadata.Contains("sha256="+hash) && metadata.Contains("version=0.7.3"),"Update URL pins release identity");
    Check(controller.Download("0.7.2",hash) is ConflictObjectResult,"Changed version never served");
    Check(controller.Download("0.7.3",new string('0',64)) is ConflictObjectResult,"Changed hash never served");
    using(var stream=((FileStreamResult)controller.Download("0.7.3",hash)).FileStream)Check(stream.Length==2048,"ZIP bytes verified");
    File.WriteAllBytes(Path.Combine(folder,file),Enumerable.Repeat((byte)1,2048).ToArray());Check(controller.Download() is NotFoundResult,"Same-size tampering rejected");
    File.WriteAllBytes(Path.Combine(folder,file),bytes);
    var updateFile="YoMi_Desktop_Pet-0.7.3-update-win-x64.zip";
    var updateBytes=Enumerable.Repeat((byte)2,4096).ToArray();
    var updateHash=Convert.ToHexString(SHA256.HashData(updateBytes)).ToLowerInvariant();
    File.WriteAllBytes(Path.Combine(folder,updateFile),updateBytes);
    File.WriteAllText(Path.Combine(folder,"release.json"),JsonSerializer.Serialize(new {version="0.7.3",fileName=file,sizeBytes=bytes.Length,sha256=hash,releasedAt=DateTime.UtcNow,backendOrigin="https://localhost:7443",
        update=new {fileName=updateFile,sizeBytes=updateBytes.Length,sha256=updateHash}}));
    var latest=JsonSerializer.SerializeToElement(((OkObjectResult)controller.Latest()).Value);
    var updateZip=latest.GetProperty("zip");
    Check(updateZip.GetProperty("url").GetString().Contains("update=true") && updateZip.GetProperty("sha256").GetString()==updateHash && updateZip.GetProperty("size").GetInt64()==updateBytes.Length,
        "App update selects runtime ZIP, never installer ZIP");
    using(var stream=((FileStreamResult)controller.Download("0.7.3",hash)).FileStream)
        Check(stream.Length==bytes.Length && stream.ReadByte()==0,"Website still serves installer ZIP");
    var updateDownload=(FileStreamResult)controller.Download("0.7.3",updateHash,update:true);
    using(var stream=updateDownload.FileStream)
        Check(updateDownload.FileDownloadName==updateFile && stream.Length==updateBytes.Length && stream.ReadByte()==2 && updateDownload.EntityTag.Tag.Value.Contains(updateHash),"App download serves verified runtime bytes with runtime ETag");
    Check(controller.Download("0.7.3",hash,update:true) is ConflictObjectResult,"Installer hash cannot pin runtime update download");
    Check(controller.Download("0.7.2",updateHash,update:true) is ConflictObjectResult,"Runtime update rejects obsolete version");
    File.WriteAllBytes(Path.Combine(folder,updateFile),Enumerable.Repeat((byte)3,updateBytes.Length).ToArray());
    Check(controller.Download("0.7.3",updateHash,update:true) is NotFoundResult,"Same-size runtime tampering rejected");
    DesktopGoogleExchange.Validate("code",new string('a',43),"http://127.0.0.1:50000/oauth/callback");count++;
    foreach(var redirect in new[]{"https://evil.invalid/oauth/callback","http://localhost:50000/oauth/callback","http://127.0.0.1:80/oauth/callback","http://u:p@127.0.0.1:50000/oauth/callback","http://127.0.0.1:50000/oauth/callback?x=1"}) {
        bool rejected=false;try{DesktopGoogleExchange.Validate("code",new string('a',43),redirect);}catch(ArgumentException){rejected=true;}Check(rejected,"Unsafe OAuth callback rejected");
    }

    var privateFolder=Path.Combine(root,"local-secrets");Directory.CreateDirectory(privateFolder);
    File.WriteAllText(Path.Combine(privateFolder,"desktop-google.json"),"{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\",\"client_secret\":\"fixture-secret\"}}");
    var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{{"DesktopPet:GoogleClientIds:0","fixture.apps.googleusercontent.com"}}).Build();
    var exchange=new DesktopGoogleExchange(config,new TestEnvironment{ContentRootPath=Path.Combine(root,"site"),WebRootPath=Path.Combine(root,"site","wwwroot")});
    var google=new DesktopGoogleController(exchange,new DesktopGoogleVerifier(config));
    google.ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext()};google.Request.Scheme="https";
    var publicConfig=JsonSerializer.Serialize(((OkObjectResult)google.Config()).Value);
    Check(publicConfig=="{\"clientId\":\"fixture.apps.googleusercontent.com\"}","Public endpoint exposes only client ID, never secret");
    google.Request.Scheme="http";Check(google.Config() is BadRequestResult,"OAuth config requires HTTPS");
    google.Request.Scheme="https";File.Delete(Path.Combine(privateFolder,"desktop-google.json"));
    Check(google.Config() is ObjectResult{StatusCode:503},"Missing secret gives a safe service error");
    Console.WriteLine($"API checks: {count} passed; no database accessed.");
}finally{Directory.Delete(root,true);}


sealed class TestEnvironment : IWebHostEnvironment {
 public string ApplicationName{get;set;}="Tests";public string EnvironmentName{get;set;}="Development";
 public string ContentRootPath{get;set;} public string WebRootPath{get;set;}
 public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}
