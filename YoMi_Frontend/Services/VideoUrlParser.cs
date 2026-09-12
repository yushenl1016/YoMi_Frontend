using System.Text.RegularExpressions;

namespace YoMi_Frontend.Services
{
    public sealed class ParsedVideoUrl
    {
        public bool IsValid { get; init; }
        public string Platform { get; init; } = "unknown";
        public string EmbedUrl { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string Error { get; init; }
    }

    public static class VideoUrlParser
    {
        public static ParsedVideoUrl Parse(string url)
        {
            var value = Regex.Replace(url ?? string.Empty, "[\\u200b\\u200c\\u200d\\ufeff]", string.Empty).Trim(' ', '\"', '\'', '<', '>');
            var urlMatch = Regex.Match(value, "https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase);
            if (urlMatch.Success) value = urlMatch.Value;

            if (Regex.IsMatch(value, "youtube\\.com|youtu\\.be", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(value, "(?:youtube\\.com/(?:watch\\?(?:.*&)?v=|shorts/|embed/)|youtu\\.be/)([a-zA-Z0-9_-]{11})", RegexOptions.IgnoreCase);
                if (!match.Success) return Invalid("youtube", "無法解析 YouTube 影片 ID");
                var id = match.Groups[1].Value;
                return Valid("youtube", $"YouTube: {id}", $"https://www.youtube.com/embed/{id}?autoplay=1&mute=1&loop=1&playlist={id}&controls=0&rel=0&modestbranding=1&playsinline=1");
            }

            if (Regex.IsMatch(value, "bilibili\\.com", RegexOptions.IgnoreCase))
            {
                var bv = Regex.Match(value, "bilibili\\.com/video/(BV[a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                if (bv.Success) return Valid("bilibili", $"Bilibili: {bv.Groups[1].Value}", $"https://player.bilibili.com/player.html?bvid={bv.Groups[1].Value}&autoplay=1&muted=1&danmaku=0&high_quality=1");
                var av = Regex.Match(value, "bilibili\\.com/video/av(\\d+)", RegexOptions.IgnoreCase);
                if (av.Success) return Valid("bilibili", $"Bilibili: av{av.Groups[1].Value}", $"https://player.bilibili.com/player.html?aid={av.Groups[1].Value}&autoplay=1&muted=1&danmaku=0&high_quality=1");
                return Invalid("bilibili", "無法解析 Bilibili 影片 ID");
            }

            if (Regex.IsMatch(value, "douyin\\.com|tiktok\\.com", RegexOptions.IgnoreCase))
            {
                return Invalid("douyin", "抖音／TikTok 暫不支援網頁嵌入，請下載 MP4 後上傳");
            }

            return Invalid("unknown", "目前支援 YouTube 與 Bilibili 網址");
        }

        private static ParsedVideoUrl Valid(string platform, string name, string url) =>
            new() { IsValid = true, Platform = platform, DisplayName = name, EmbedUrl = url };

        private static ParsedVideoUrl Invalid(string platform, string error) =>
            new() { Platform = platform, DisplayName = platform, Error = error };

        public static void SelfCheck()
        {
            if (!Parse("https://youtu.be/dQw4w9WgXcQ").IsValid ||
                !Parse("https://www.bilibili.com/video/BV1xx411c7mD").IsValid ||
                Parse("https://www.tiktok.com/example").IsValid)
            {
                throw new System.InvalidOperationException("Video URL rules are inconsistent.");
            }
        }
    }
}
