using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using YoMi_Frontend.Services;

namespace YoMi_Frontend.Controllers
{
    [AllowAnonymous]
    public sealed class DesktopDownloadController : Controller
    {
        private readonly DesktopPetReleaseService _releases;
        public DesktopDownloadController(DesktopPetReleaseService releases) { _releases = releases; }

        [HttpGet("desktop-pet")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult Index() => View(_releases.Read());

        [AcceptVerbs("GET", "HEAD", Route = "desktop-pet/download")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult Download(string version = null, string sha256 = null, bool update = false)
        {
            var release = _releases.Read();
            if (release == null) return NotFound();
            var filePath = update && release.Update != null ? release.Update.FilePath : release.FilePath;
            var fileName = update && release.Update != null ? release.Update.FileName : release.FileName;
            var expectedHash = update && release.Update != null ? release.Update.Sha256 : release.Sha256;
            if ((version != null && version != release.Version) || (sha256 != null && sha256 != expectedHash)) return Conflict(new { error = "發行版本已更新，請重新檢查更新。" });
            FileStream stream = null;
            try
            {
                // Verify and serve the same open file; a same-size replacement must not inherit the advertised digest.
                stream = System.IO.File.OpenRead(filePath);
                using var sha = SHA256.Create();
                var digest = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                if (digest != expectedHash) { stream.Dispose(); return NotFound(); }
                stream.Position = 0;
                return new FileStreamResult(stream, "application/zip")
                {
                    FileDownloadName = fileName,
                    EnableRangeProcessing = true,
                    EntityTag = new EntityTagHeaderValue("\"" + digest + "\""),
                    LastModified = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(filePath))
                };
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                stream?.Dispose();
                return NotFound();
            }
        }

        [HttpGet("api/desktop/releases/latest")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult Latest()
        {
            var release = _releases.Read();
            if (release == null) return NotFound(new { error = "目前沒有可用的更新包。" });
            var hash = release.Update?.Sha256 ?? release.Sha256;
            return Ok(new { version = release.Version, backendOrigin = release.BackendOrigin,
                zip = new { url = "/desktop-pet/download?" + (release.Update != null ? "update=true&" : "") + "version=" + Uri.EscapeDataString(release.Version) + "&sha256=" + hash,
                    size = release.Update?.SizeBytes ?? release.SizeBytes, sha256 = hash } });
        }
    }
}
