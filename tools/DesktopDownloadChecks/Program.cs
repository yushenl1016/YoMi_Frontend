using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using YoMi_Frontend.Services;
using YoMi_Frontend.Controllers;

var root = Path.Combine(Path.GetTempPath(), "yomi-download-" + Guid.NewGuid().ToString("N"));
var folder = Path.Combine(root, "App_Data", "DesktopPetReleases");
Directory.CreateDirectory(folder);
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
try
{
    var service = new DesktopPetReleaseService(root);
    Check(service.Read() == null, "Missing release is unavailable");
    File.WriteAllText(Path.Combine(folder, "release.json"), "malformed");
    Check(service.Read() == null, "Malformed release is unavailable");
    var file = "YoMi_Desktop_Pet-0.7.0-win-x64.zip";
    File.WriteAllBytes(Path.Combine(folder, file), new byte[] { 80, 75, 3, 4 });
    object Manifest(string name, long size = 4, string hash = null, string origin = "https://localhost:5101") => new { version = "0.7.0", fileName = name, sizeBytes = size, sha256 = hash ?? Convert.ToHexString(SHA256.HashData(new byte[] { 80, 75, 3, 4 })).ToLowerInvariant(), releasedAt = DateTimeOffset.UtcNow, backendOrigin = origin };
    void Save(object value) => File.WriteAllText(Path.Combine(folder,"release.json"),JsonSerializer.Serialize(value));
    Save(Manifest(file));
    Check(service.Read()?.IsLocalBuild == true, "Local release detected from origin");
    var controller = new DesktopDownloadController(service);
    var download = controller.Download() as FileStreamResult;
    Check(download?.FileDownloadName == file && download.ContentType == "application/zip" && download.EnableRangeProcessing, "ZIP response supports resumable download");
    Check(download.EntityTag.Tag.Value.Contains(Convert.ToHexString(SHA256.HashData(new byte[] { 80, 75, 3, 4 })).ToLowerInvariant()), "Release hash used as ETag");
    Check(controller.Index() is ViewResult { Model: not null }, "MVC page receives release");
    Check(download.FileStream.ReadByte() == 80, "Verified stream is rewound before download");
    download.FileStream.Dispose();
    File.WriteAllBytes(Path.Combine(folder,file), new byte[] { 80, 75, 3, 5 });
    Check(controller.Download() is NotFoundResult, "Same-size modified ZIP is never served");
    File.WriteAllBytes(Path.Combine(folder,file), new byte[] { 80, 75, 3, 4 });
    foreach (var invalid in new[] { "../../private.zip", "/private.zip", "C:\\private.zip", "YoMi_Desktop_Pet-9.0.0-win-x64.zip" })
    { Save(Manifest(invalid)); Check(service.Read() == null,"Invalid archive name rejected"); }
    Save(Manifest(file, size: 99)); Check(service.Read() == null, "Truncated file unavailable");
    Save(Manifest(file, hash: "bad")); Check(service.Read() == null, "Invalid digest rejected");
    Save(Manifest(file, origin: "http://example.invalid")); Check(service.Read() == null, "Non-HTTPS origin rejected");
    var updateFile="YoMi_Desktop_Pet-0.7.0-update-win-x64.zip";
    File.WriteAllBytes(Path.Combine(folder,updateFile),new byte[] {80,75,3,4});
    var updateHash=Convert.ToHexString(SHA256.HashData(new byte[] {80,75,3,4})).ToLowerInvariant();
    object WithUpdate(string name,long size=4,string hash=null) => new { version="0.7.0",fileName=file,sizeBytes=4,sha256=updateHash,releasedAt=DateTimeOffset.UtcNow,backendOrigin="https://localhost:5101",
        update=new {fileName=name,sizeBytes=size,sha256=hash??updateHash} };
    Save(WithUpdate(updateFile));
    Check(service.Read()?.Update?.FileName==updateFile,"Separate runtime update archive is recognized");
    foreach(var invalid in new[] {"../private.zip",file,"YoMi_Desktop_Pet-9.0.0-update-win-x64.zip"})
    { Save(WithUpdate(invalid)); Check(service.Read()==null,"Unsafe update archive rejects entire release"); }
    Save(WithUpdate(updateFile,hash:"bad")); Check(service.Read()==null,"Invalid update digest rejected");
    Save(WithUpdate(updateFile,size:0)); Check(service.Read()==null,"Empty update rejected");
    Save(WithUpdate(updateFile,size:1073741825)); Check(service.Read()==null,"Oversized update rejected");
    Save(WithUpdate(updateFile,size:99)); Check(service.Read()==null,"Truncated update rejected");
    Save(WithUpdate(updateFile)); File.Delete(Path.Combine(folder,updateFile));
    Check(controller.Latest() is NotFoundObjectResult && controller.Download(update:true) is NotFoundResult,"Missing runtime update is never replaced by installer ZIP");
    Save(Manifest(file)); File.Delete(Path.Combine(folder,file));
    Check(controller.Download() is NotFoundResult, "Missing package cannot be downloaded");
    Console.WriteLine($"Desktop download checks: {checks} passed; no database accessed.");
}
finally { Directory.Delete(root,true); }
