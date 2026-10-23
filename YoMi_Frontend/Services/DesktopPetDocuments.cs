using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YoMi_Frontend.Services
{
    public static class DesktopPetDocuments
    {
        private static readonly string[] ProfileKeys = { "profile", "focus", "inventory", "equipped", "baseLayout", "games" };
        private static readonly string[] MusicKeys = { "version", "favorites", "playlists", "settings" };
        public static void Validate(string kind, JsonElement payload)
        {
            var keys = kind == "profile" ? ProfileKeys : kind == "music" ? MusicKeys : throw new ArgumentException("Unknown document kind.");
            if (payload.ValueKind != JsonValueKind.Object || payload.GetRawText().Length > 800000 ||
                payload.EnumerateObject().Any(x => !keys.Contains(x.Name)) ||
                payload.EnumerateObject().Select(x => x.Name).Distinct().Count() != payload.EnumerateObject().Count())
                throw new ArgumentException("Invalid document.");
            if (kind == "profile")
            {
                foreach (var name in new[] { "profile", "focus", "baseLayout", "games" })
                    if (payload.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid progress object.");
                foreach (var name in new[] { "inventory", "equipped" })
                    if (payload.TryGetProperty(name, out var value) && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 10000 || value.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))) throw new ArgumentException("Invalid items.");
                if (payload.TryGetProperty("focus", out var focus))
                    foreach (var value in focus.EnumerateObject())
                    {
                        if (value.Name == "isRunning") { if (value.Value.ValueKind != JsonValueKind.False) throw new ArgumentException("Running state is device-specific."); }
                        else if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 9007199254740991) throw new ArgumentException("Invalid focus value.");
                    }
            }
            else
            {
                if (payload.TryGetProperty("favorites", out var favorites)) Tracks(favorites);
                if (payload.TryGetProperty("playlists", out var lists))
                {
                    if (lists.ValueKind != JsonValueKind.Array || lists.GetArrayLength() > 60) throw new ArgumentException("Invalid playlists.");
                    foreach (var list in lists.EnumerateArray())
                        if (list.ValueKind != JsonValueKind.Object || !list.TryGetProperty("tracks", out var tracks)) throw new ArgumentException("Invalid playlist.");
                        else Tracks(tracks);
                }
            }
        }
        private static void Tracks(JsonElement tracks)
        {
            if (tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() > 1000) throw new ArgumentException("Invalid tracks.");
            foreach (var track in tracks.EnumerateArray())
                if (track.ValueKind != JsonValueKind.Object || !track.TryGetProperty("source", out var source) || source.GetString() != "youtube" || track.TryGetProperty("path", out _) ||
                    !track.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !System.Text.RegularExpressions.Regex.IsMatch(id.GetString(), @"^[\w-]{11}$")) throw new ArgumentException("Only online tracks can be synchronized.");
        }
        // Imported v0 snapshots remain backed up; device fields never leave the API again.
        public static JsonElement Project(string kind, string raw)
        {
            var source = JsonNode.Parse(raw) as JsonObject ?? throw new ArgumentException("Invalid stored document.");
            var result = new JsonObject();
            foreach (var key in kind == "profile" ? ProfileKeys : MusicKeys)
                if (source[key] != null) result[key] = JsonNode.Parse(source[key].ToJsonString());
            if (kind == "profile" && result["focus"] is JsonObject focus) focus["isRunning"] = false;
            if (kind == "music")
            {
                JsonArray Online(JsonNode tracks) => new JsonArray((tracks as JsonArray ?? new JsonArray())
                    .Where(x => x?["source"]?.GetValue<string>() == "youtube" && x?["path"] == null).Select(x => JsonNode.Parse(x.ToJsonString())).ToArray());
                result["favorites"] = Online(result["favorites"]);
                if (result["playlists"] is JsonArray playlists)
                    foreach (var list in playlists) if (list is JsonObject obj) obj["tracks"] = Online(obj["tracks"]);
            }
            return JsonDocument.Parse(result.ToJsonString()).RootElement.Clone();
        }
    }
}
