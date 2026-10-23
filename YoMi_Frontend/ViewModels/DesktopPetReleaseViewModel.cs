using System;
using System.Text.Json.Serialization;

namespace YoMi_Frontend.ViewModels
{
    public sealed class DesktopPetUpdateViewModel
    {
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        [JsonIgnore] public string FilePath { get; set; }
    }

    public sealed class DesktopPetReleaseViewModel
    {
        public string Version { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public DateTimeOffset ReleasedAt { get; set; }
        public string BackendOrigin { get; set; }
        public DesktopPetUpdateViewModel Update { get; set; }
        [JsonIgnore] public string FilePath { get; set; }
        [JsonIgnore] public bool IsLocalBuild => Uri.TryCreate(BackendOrigin, UriKind.Absolute, out var uri) && uri.IsLoopback;
        [JsonIgnore] public string SizeLabel => (SizeBytes / 1024d / 1024d).ToString("0.0") + " MB";
    }
}
