using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Services
{
    public sealed class DesktopPetReleaseService
    {
        private readonly string _directory;
        public DesktopPetReleaseService(string contentRoot)
        {
            _directory = Path.Combine(contentRoot, "App_Data", "DesktopPetReleases");
        }

        private string ValidatedArchive(string name, long size, string hash, string expectedName)
        {
            if (name != expectedName || size <= 0 || size > 1073741824 || !Regex.IsMatch(hash ?? "", @"\A[a-f0-9]{64}\z")) return null;
            var filePath = Path.Combine(_directory, name);
            var archive = new FileInfo(filePath);
            return archive.Exists && archive.Length == size && (archive.Attributes & FileAttributes.ReparsePoint) == 0 ? filePath : null;
        }

        public DesktopPetReleaseViewModel Read()
        {
            try
            {
                var manifest = Path.Combine(_directory, "release.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > 16384) return null;
                var release = JsonSerializer.Deserialize<DesktopPetReleaseViewModel>(File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (release == null || !Regex.IsMatch(release.Version ?? "", @"\A\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?\z") ||
                    release.ReleasedAt == default ||
                    !Uri.TryCreate(release.BackendOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
                    origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0) return null;
                release.FilePath = ValidatedArchive(release.FileName, release.SizeBytes, release.Sha256, $"YoMi_Desktop_Pet-{release.Version}-win-x64.zip");
                if (release.FilePath == null) return null;
                if (release.Update != null)
                {
                    release.Update.FilePath = ValidatedArchive(release.Update.FileName, release.Update.SizeBytes, release.Update.Sha256, $"YoMi_Desktop_Pet-{release.Version}-update-win-x64.zip");
                    if (release.Update.FilePath == null) return null;
                }
                return release;
            }
            catch (Exception error) when (error is IOException || error is JsonException || error is UnauthorizedAccessException || error is NotSupportedException)
            {
                return null;
            }
        }
    }
}
