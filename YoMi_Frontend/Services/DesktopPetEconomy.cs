using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace YoMi_Frontend.Services
{
    public sealed class DesktopEconomyConflictException : Exception
    {
        public string Code { get; }
        public DesktopEconomyConflictException(string message, string code = "operation_conflict") : base(message) { Code = code; }
    }
    public static class DesktopPetEconomy
    {
        public sealed record Item(int Price, string Category);
        public static readonly IReadOnlyDictionary<string, Item> Catalog = BuildCatalog();
        static Dictionary<string, Item> BuildCatalog()
        {
            var result = new Dictionary<string, Item>();
            void Add(string prefix, string names, int price, string category) { foreach (var id in names.Split(' ')) result.Add(prefix + id, new Item(price, category)); }
            Add("outfit-", "sailor yukata maid esports christmas qipao strawberry", 360, "outfit");
            Add("hw-", "headphones cat-ears flower-crown big-bow beret santa-hat tiara", 220, "headwear");
            Add("face-", "happy blink sleepy pout focused surprised love wink", 150, "face");
            Add("", "sakura-keycap", 60, "keycap"); Add("keycap-", "mint midnight", 120, "keycap"); Add("keycap-", "candy", 160, "keycap");
            Add("background-", "bar beach", 280, "background"); Add("decor-", "plant fruit-soda seashell", 100, "decor"); Add("decor-", "star-lamp", 130, "decor");
            foreach (var pair in new[] { ("neon-lamp", 0), ("energy-cup", 90), ("holo-shelf", 130), ("moon-umbrella", 180), ("cyber-bloom", 240) }) Add("", pair.Item1, pair.Item2, "decor");
            return result;
        }
        public static readonly Dictionary<string, int[]> GameMinimums = new()
        {
            ["schulte"] = new[] {8,12,18}, ["2048"] = new[] {30,60,120}, ["minesweeper"] = new[] {8,20,35},
            ["sudoku"] = new[] {45,90,150}, ["match3"] = new[] {20,25,25}, ["memory"] = new[] {10,14,18}, ["slide15"] = new[] {15,40,90}
        };
        public static JsonObject NewProfile() => JsonNode.Parse(@"{""profile"":{""displayName"":""悠咪"",""accent"":""cyan""},""focus"":{""isRunning"":false,""activeSeconds"":0,""completedSessions"":0,""coins"":120,""unclaimedRewards"":0,""keyCount"":0},""inventory"":[""neon-lamp""],""equipped"":[""neon-lamp""],""baseLayout"":{""version"":1,""theme"":""yumi-esports"",""placedItems"":[""neon-lamp""]},""games"":{""day"":"""",""used"":0,""bonus"":0}}") as JsonObject;
        public static void CompleteProfile(JsonObject profile)
        {
            var defaults=NewProfile();
            foreach(var pair in defaults)
                if(profile[pair.Key]==null) profile[pair.Key]=JsonNode.Parse(pair.Value.ToJsonString());
            foreach(var key in new[] {"profile","focus","games"})
                if(profile[key] is JsonObject target)
                    foreach(var pair in defaults[key].AsObject())
                        if(target[pair.Key]==null) target[pair.Key]=JsonNode.Parse(pair.Value.ToJsonString());
        }
        public static long Number(JsonNode node, long fallback = 0)
        {
            if (node == null) return fallback;
            if (!double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || number > 9007199254740991 || number != Math.Floor(number)) throw new ArgumentException("無效的進度數字。");
            return (long)number;
        }
        public static string Text(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException("缺少或無效的 " + key);
        public static DateTimeOffset Timestamp(string value)
        {
            if(value==null || !System.Text.RegularExpressions.Regex.IsMatch(value,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") || !DateTimeOffset.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var timestamp)) throw new ArgumentException("時間必須是含時區的 ISO 格式。");
            return timestamp;
        }
        public static void Keys(JsonObject obj, params string[] keys)
        {
            if (obj.Any(x => !keys.Contains(x.Key))) throw new ArgumentException("操作包含未允許的欄位。");
        }
        static JsonObject Object(JsonObject root, string key) { if (root[key] == null) root[key] = new JsonObject(); return root[key] as JsonObject ?? throw new ArgumentException("無效的進度物件。"); }
        static JsonArray Array(JsonObject root, string key) { if (root[key] == null) root[key] = new JsonArray(); return root[key] as JsonArray ?? throw new ArgumentException("無效的物品清單。"); }
        static bool Owns(JsonArray inventory, string id) => inventory.Any(x => x?.GetValue<string>() == id);
        static void Add(JsonObject obj, string key, long delta) { var next = checked(Number(obj[key]) + delta); if (next < 0 || next > 9007199254740991) throw new DesktopEconomyConflictException("專注幣或可用數量不足。"); obj[key] = next; }
        static int Difficulty(string value) => value switch { "easy" => 0, "medium" => 1, "hard" => 2, _ => throw new ArgumentException("無效的遊戲難度。") };
        public static int GameReward(string game, string difficulty, double seconds, bool won)
        {
            if (!GameMinimums.TryGetValue(game, out var minimums)) throw new ArgumentException("未知的遊戲。");
            var level = Difficulty(difficulty); return won && seconds >= minimums[level] ? new[] {12,20,30}[level] : seconds >= 15 ? 3 : 0;
        }
        public static JsonObject Apply(JsonObject p, string type, JsonObject input, DateTimeOffset now, long acceptedSeconds = 0, long acceptedKeys = 0, JsonObject run = null)
        {
            var focus = Object(p, "focus"); var inventory = Array(p, "inventory"); var games = Object(p, "games");
            var before = Number(focus["coins"]); var receipt = new JsonObject { ["type"] = type };
            switch (type)
            {
                case "focus":
                    Keys(input, "startedAt", "endedAt", "activeSeconds", "keyCount");
                    var total = checked(Number(p["_economy"]?["carrySeconds"] ?? focus["activeSeconds"]) + acceptedSeconds); var sessions = total / 1800;
                    focus["activeSeconds"] = total % 1800; if (p["_economy"] is JsonObject meta) meta["carrySeconds"] = total % 1800; Add(focus,"completedSessions",sessions); Add(focus,"coins",sessions*45); Add(focus,"unclaimedRewards",sessions); Add(focus,"keyCount",acceptedKeys);
                    receipt["acceptedSeconds"] = acceptedSeconds; receipt["acceptedKeys"] = acceptedKeys; break;
                case "claim":
                    Keys(input); Add(focus,"unclaimedRewards",-1); Add(focus,"coins",15); break;
                case "purchase":
                    Keys(input,"itemId","slot"); var itemId = Text(input,"itemId");
                    if (!Catalog.TryGetValue(itemId, out var item)) throw new ArgumentException("未知的商城物品。");
                    if (Owns(inventory,itemId)) throw new DesktopEconomyConflictException("這件已經擁有了。");
                    Add(focus,"coins",-item.Price); inventory.Add(itemId); Equip(p,input); receipt["itemId"] = itemId; break;
                case "equip": Keys(input,"itemId","slot","clearSlot"); Equip(p,input); break;
                case "tickets":
                    Keys(input,"quantity"); var quantity = Number(input["quantity"]); if (quantity < 1 || quantity > 20) throw new ArgumentException("一次可購買 1 到 20 張門票。");
                    Add(focus,"coins",-quantity*10); Add(games,"bonus",quantity); receipt["quantity"] = quantity; receipt["cost"] = quantity*10; break;
                case "game-start":
                    Keys(input,"gameId","difficulty"); var game = Text(input,"gameId"); var difficulty = Text(input,"difficulty"); GameReward(game,difficulty,0,false);
                    var day = now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
                    var used = games["day"]?.GetValue<string>() == day ? Number(games["used"]) : 0;
                    if (used < 9) games["used"] = used + 1; else { Add(games,"bonus",-1); games["used"] = used; }
                    games["day"] = day; receipt["gameId"] = game; receipt["difficulty"] = difficulty; receipt["startedAt"] = now.ToString("O"); break;
                case "game-finish":
                    Keys(input,"runId","won","finishedAt"); if (run == null) throw new DesktopEconomyConflictException("找不到這局遊戲，或已經結算。");
                    if (input["won"] is not JsonValue wonValue || !wonValue.TryGetValue<bool>(out var won)) throw new ArgumentException("無效的遊戲結果。");
                    var startedAt=Timestamp(Text(run,"startedAt")); var finishedAt=Timestamp(Text(input,"finishedAt"));
                    if(finishedAt<startedAt || finishedAt>now) throw new ArgumentException("遊戲完成時間必須介於開始與目前時間。");
                    var elapsed = (finishedAt-startedAt).TotalSeconds;
                    // shortcut: client reports won; add board replay validation if competitive rewards are introduced.
                    Add(focus,"coins",GameReward(Text(run,"gameId"),Text(run,"difficulty"),elapsed,won)); receipt["runId"] = Text(input,"runId"); receipt["difficulty"] = Text(run,"difficulty"); break;
                case "preferences":
                    Keys(input,"profile","baseLayout");
                    if (input["profile"] != null)
                    {
                        var prefs = input["profile"] as JsonObject ?? throw new ArgumentException("無效的偏好。"); Keys(prefs,"displayName","accent");
                        foreach (var pair in prefs) { var value = Text(prefs,pair.Key); if (value.Length > (pair.Key == "displayName" ? 40 : 24) || value.Any(char.IsControl)) throw new ArgumentException("名稱或色彩無效。"); Object(p,"profile")[pair.Key] = value; }
                    }
                    if (input["baseLayout"] != null) p["baseLayout"] = Scene(input["baseLayout"] as JsonObject ?? throw new ArgumentException("無效的場景。"), inventory, Array(p,"equipped"));
                    break;
                default: throw new ArgumentException("未知的操作類型。");
            }
            focus["isRunning"] = false; receipt["coins"] = Number(focus["coins"]) - before; return receipt;
        }
        static void Equip(JsonObject p, JsonObject input)
        {
            var owned = Array(p,"inventory"); var equipped = Array(p,"equipped").Select(x=>x.GetValue<string>()).ToList();
            string Slot(string value) => value.StartsWith("idle:") ? "idle" : value.StartsWith("typing:") ? "typing" : Catalog.TryGetValue(value,out var item) ? item.Category : null;
            if (input["clearSlot"] != null)
            {
                var slot = Text(input,"clearSlot"); if (!new[] {"outfit","headwear","keycap","background","idle","typing"}.Contains(slot)) throw new ArgumentException("未知的裝備槽。");
                equipped.RemoveAll(x=>Slot(x)==slot);
            }
            else
            {
                var id=Text(input,"itemId"); if (!Catalog.TryGetValue(id,out var item) || !Owns(owned,id)) throw new DesktopEconomyConflictException("尚未擁有這件物品。");
                var slot = item.Category; var tag=id;
                if (slot=="face") { slot=input["slot"]==null ? "idle" : Text(input,"slot"); if(slot!="idle" && slot!="typing") throw new ArgumentException("無效的表情槽。"); tag=slot+":"+id; }
                if (equipped.Contains(tag)) equipped.RemoveAll(x=>x==tag);
                else { if(slot!="decor") equipped.RemoveAll(x=>Slot(x)==slot); equipped.Add(tag); }
            }
            p["equipped"]=new JsonArray(equipped.Select(x=>JsonValue.Create(x)).ToArray());
            p["baseLayout"]=Scene(p["baseLayout"] as JsonObject ?? new JsonObject(),owned,Array(p,"equipped"));
        }
        static JsonObject Scene(JsonObject input, JsonArray owned, JsonArray equipped)
        {
            var placements = new JsonArray(); var seen=new HashSet<string>();
            if (input["placements"] is JsonArray rows)
                foreach(var row in rows.Take(64))
                {
                    if(row is not JsonObject obj) continue; var id=Text(obj,"id"); if(!Owns(owned,id)||!Catalog.TryGetValue(id,out var item)||item.Category!="decor"||!seen.Add(id)) continue;
                    double Coordinate(string key,double fallback,double min,double max) { if(obj[key] is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n)) return Math.Round(Math.Clamp(n,min,max),4); return fallback; }
                    placements.Add(new JsonObject { ["id"]=id,["x"]=Coordinate("x",.2,.04,.96),["y"]=Coordinate("y",.95,.12,.99),["scale"]=Coordinate("scale",1,.5,1.8),["flip"]=obj["flip"]?.ToJsonString()=="true",["layer"]=obj["layer"]?.ToString()=="back"?"back":"front" });
                    if(placements.Count==12) break;
                }
            var theme=equipped.Select(x=>x.GetValue<string>()).LastOrDefault(x=>Catalog.TryGetValue(x,out var item)&&item.Category=="background")??"yumi-esports";
            return new JsonObject { ["version"]=2,["theme"]=theme,["placements"]=placements };
        }
    }
}


