using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace YoMi_Frontend.Services
{
    // shortcut: rooms are process-local; use shared room state before deploying multiple website workers.
    public sealed class DesktopRooms : IDisposable
    {
        private const int MaxMembers = 12;
        private const int MaxFrameBytes = 32768;
        private const long ResumeGraceMs = 120000;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        private readonly object _gate = new object();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private readonly HashSet<Connection> _connections = new HashSet<Connection>();
        private readonly IDataProtector _protector;
        private readonly Timer _timer;
        private bool _disposed;
        private long _directoryRevision;
        private readonly Dictionary<string, DirectoryRow> _directory = new Dictionary<string, DirectoryRow>();
        private readonly Dictionary<string, Room> _directoryDirty = new Dictionary<string, Room>();
        private sealed record DirectoryRow(string PublicId, string Title, string HostName, long CreatedAt, int Count, int OnlineCount, int Capacity);
        private long _presenceAt;
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static readonly HashSet<string> Statuses = new HashSet<string>(new[] { "focus", "study", "work", "code", "read", "game", "rest" });
        private static readonly HashSet<string> Faces = new HashSet<string>(new[] { "happy", "blink", "sleepy", "pout", "focused", "surprised", "love", "wink" });
        private static readonly HashSet<string> Headwear = new HashSet<string>(new[] { "headphones", "cat-ears", "flower-crown", "big-bow", "beret", "santa-hat", "tiara" });
        private static readonly HashSet<string> Outfits = new HashSet<string>(new[] { "sailor", "yukata", "maid", "esports", "christmas", "qipao", "strawberry" });
        private static readonly Dictionary<string, (double X, double Y)> Decor = new Dictionary<string, (double, double)>
        {
            ["neon-lamp"] = (.86, .35), ["energy-cup"] = (.85, .97), ["holo-shelf"] = (.2, .4), ["moon-umbrella"] = (.86, .76), ["cyber-bloom"] = (.12, .98),
            ["decor-plant"] = (.12, .94), ["decor-star-lamp"] = (.87, .92), ["decor-fruit-soda"] = (.82, .95), ["decor-seashell"] = (.17, .92)
        };
        private sealed class Room
        {
            public string Code, Title, Visibility = "private", PublicId = RandomHex(16);
            public long CreatedAt = Now;
            public Dictionary<string, Member> Members = new Dictionary<string, Member>();
            public long ChangedAt;
        }
        private sealed class Member
        {
            public int UserId, Seat, KeyCount;
            public string Id, Token, Name, Status = "focus", Avatar;
            public bool Host, Online, IsTyping, Listening;
            public long JoinedAt, OfflineSince, LastKeyAt;
            public string[] Items = Array.Empty<string>();
            public JsonElement Layout;
            public Connection Connection;
        }
        private sealed class Connection
        {
            public bool Retired, DirectorySubscribed;
            public string RequestId;
            public int UserId, IdentityCount, FrameCount;
            public string Code, MemberId, Name;
            public long IdentityWindow, FrameWindow, LastChat, LastKeyboard;
            public WebSocket Socket;
            public Channel<IEnumerable<byte[]>> Outbox = Channel.CreateBounded<IEnumerable<byte[]>>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        }
        private sealed class ResumeProof
        {
            public int UserId { get; set; }
            public string Code { get; set; }
            public string MemberId { get; set; }
            public string Token { get; set; }
            public bool Host { get; set; }
            public long Expires { get; set; }
            public string Visibility { get; set; }
            public string PublicId { get; set; }
            public long CreatedAt { get; set; }
        }
        public DesktopRooms(IDataProtectionProvider protection)
        {
            _protector = protection.CreateProtector("YoMi.Desktop.RoomResume.v1");
            _timer = new Timer(_ => Sweep(), null, 250, 250);
        }
        private static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
        private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        private static string Text(JsonElement value, string name) => Text(Get(value, name));
        private static bool Bool(JsonElement value, string name) => Get(value, name).ValueKind == JsonValueKind.True;
        private static string Clean(string value, int max, string fallback = "")
        { var text = (value ?? "").Trim().Replace("<", "").Replace(">", ""); return text.Length == 0 ? fallback : text.Substring(0, Math.Min(max, text.Length)); }
        private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
        private static int? Seat(JsonElement payload) => Get(payload, "seat").TryGetInt32Safe(out var seat) && seat >= 0 && seat < MaxMembers ? seat : null;
        private static bool PublicItem(string id)
        {
            if (id == null) return false;
            if (Decor.ContainsKey(id) || new[] { "sakura-keycap", "keycap-mint", "keycap-midnight", "keycap-candy", "background-bar", "background-beach" }.Contains(id)) return true;
            if (id.StartsWith("outfit-")) return Outfits.Contains(id.Substring(7));
            if (id.StartsWith("hw-")) return Headwear.Contains(id.Substring(3));
            var face = Regex.Match(id, "^(idle|typing):face-([a-z-]+)$");
            return face.Success && Faces.Contains(face.Groups[2].Value);
        }
        private static string[] Items(JsonElement payload, string[] owned)
        {
            var input = Get(payload, "equippedItems"); if (input.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var inventory = new HashSet<string>(owned ?? Array.Empty<string>());
            return input.EnumerateArray().Select(Text).Where(PublicItem).Where(id => inventory.Contains(id.Contains(':') ? id.Substring(id.IndexOf(':') + 1) : id)).Distinct().Take(24).ToArray();
        }
        private static double Number(JsonElement row, string key, double fallback, double min, double max)
        { var value = Get(row, key); return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? Math.Round(Math.Clamp(n, min, max), 4) : fallback; }
        private static JsonElement Layout(JsonElement input, string[] items)
        {
            var rows = new Dictionary<string, object>(); var placements = Get(input, "placements");
            if (placements.ValueKind == JsonValueKind.Array)
                foreach (var row in placements.EnumerateArray().Take(64))
                {
                    var id = Text(row, "id"); if (id == null || !items.Contains(id) || !Decor.TryGetValue(id, out var fallback)) continue;
                    rows[id] = new { id, x = Number(row, "x", fallback.X, .04, .96), y = Number(row, "y", fallback.Y, .12, .99), scale = Number(row, "scale", 1, .5, 1.8), flip = Bool(row, "flip"), layer = Text(row, "layer") == "back" ? "back" : "front" };
                }
            foreach (var id in items)
                if (!rows.ContainsKey(id) && Decor.TryGetValue(id, out var fallback)) rows[id] = new { id, x = fallback.X, y = fallback.Y, scale = 1, flip = false, layer = "front" };
            return JsonSerializer.SerializeToElement(new { version = 2, theme = items.LastOrDefault(x => x.StartsWith("background-")) ?? "yumi-esports", placements = rows.Values.Take(12) }, Json);
        }
        private static string Avatar(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > 16000) return null;
            if (raw.StartsWith("preset:"))
            {
                var id = raw.Substring(7);
                return id == "face-default" || (id.StartsWith("face-") && Faces.Contains(id.Substring(5))) || (id.StartsWith("hw-") && Headwear.Contains(id.Substring(3))) ? raw : null;
            }
            var match = Regex.Match(raw, @"^data:image/(jpeg|png|webp);base64,([A-Za-z0-9+/]+={0,2})$");
            if (!match.Success) return null;
            byte[] bytes; try { bytes = Convert.FromBase64String(match.Groups[2].Value); } catch (FormatException) { return null; }
            var kind = match.Groups[1].Value;
            var signature = kind == "png" ? new byte[] { 0x89, 0x50, 0x4e, 0x47 } : kind == "jpeg" ? new byte[] { 0xff, 0xd8, 0xff } : new byte[] { 0x52, 0x49, 0x46, 0x46 };
            if (!bytes.Take(signature.Length).SequenceEqual(signature) || (kind == "webp" && (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 8, 4) != "WEBP"))) return null;
            return raw;
        }
        private static string AvatarKey(string avatar) => avatar == null || avatar.StartsWith("preset:") ? avatar : "img:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(avatar))).ToLowerInvariant().Substring(0, 16);
        private static object Picture(Member member) => member.Avatar != null && !member.Avatar.StartsWith("preset:") ? new { id = member.Id, key = AvatarKey(member.Avatar), avatar = member.Avatar } : null;
        private void Send(Connection connection, string name, object payload)
        {
            SendBatch(connection, new[] { Frame(name, payload) });
        }
        private static byte[] Frame(string name, object payload) => JsonSerializer.SerializeToUtf8Bytes(new { @event = name, payload }, Json);
        private static void SendBatch(Connection c, IEnumerable<byte[]> frames)
        { if (!c.Outbox.Writer.TryWrite(frames)) c.Socket.Abort(); }
        private void Error(Connection c, string code, string message) => Send(c, "room:error", new { code, message, requestId = c.RequestId });
        private void DirectoryChanged(Room room)
        { if (room.Visibility == "public") _directoryDirty[room.PublicId] = room; }
        private void FlushDirectory()
        {
            if (_directoryDirty.Count == 0) return;
            var upserts = new List<DirectoryRow>(); var removed = new List<string>();
            foreach (var room in _directoryDirty.Values)
            {
                var online = room.Members.Values.Count(m => m.Online);
                if (!_rooms.TryGetValue(room.Code, out var active) || !ReferenceEquals(active, room) || online == 0)
                { if (_directory.Remove(room.PublicId)) removed.Add(room.PublicId); continue; }
                var row = new DirectoryRow(room.PublicId, room.Title, room.Members.Values.FirstOrDefault(m => m.Host)?.Name ?? "會員", room.CreatedAt, room.Members.Count, online, MaxMembers);
                if (!_directory.TryGetValue(room.PublicId, out var previous) || previous != row)
                { _directory[room.PublicId] = row; upserts.Add(row); }
            }
            _directoryDirty.Clear();
            var frames = new List<byte[]>();
            for (var start = 0; start < Math.Max(upserts.Count, removed.Count); start += 100)
                frames.Add(Frame("rooms:changed", new { revision = ++_directoryRevision, upserts = upserts.Skip(start).Take(100).ToArray(), removed = removed.Skip(start).Take(100).ToArray() }));
            if (frames.Count != 0)
                foreach (var c in _connections) if (c.DirectorySubscribed && !c.Retired) SendBatch(c, frames);
        }
        private static IEnumerable<byte[]> SnapshotFrames(DirectoryRow[] rows, string snapshotId, long revision)
        {
            // Serialize a captured snapshot gradually so a healthy large directory cannot fill the frame queue.
            for (var start = 0; start < Math.Max(1, rows.Length); start += 100)
                yield return Frame("rooms:snapshot", new { snapshotId, revision, part = start / 100, last = start + 100 >= rows.Length, rooms = rows.Skip(start).Take(100).ToArray() });
        }
        private void Subscribe(Connection c)
        {
            FlushDirectory(); c.DirectorySubscribed = true;
            var rows = _directory.Values.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.PublicId, StringComparer.Ordinal).ToArray();
            SendBatch(c, SnapshotFrames(rows, RandomHex(12), _directoryRevision));
        }
        private void Broadcast(Room room, string name, object payload)
        { foreach (var member in room.Members.Values) if (member.Online && member.Connection != null) Send(member.Connection, name, payload); }
        private void Update(Room room)
        {
            room.ChangedAt = 0;
            Broadcast(room, "room:update", new { code = room.Code, title = room.Title, visibility = room.Visibility, publicId = room.PublicId, serverNow = Now, members = room.Members.Values.Select(m => new
            { id = m.Id, name = m.Name, host = m.Host, equippedItems = m.Items, joinedAt = m.JoinedAt, keyCount = m.KeyCount, isTyping = m.IsTyping, online = m.Online, seat = m.Seat, status = m.Status, avatarKey = AvatarKey(m.Avatar), listening = m.Listening, baseLayout = m.Layout }).ToArray() });
        }
        private static void Changed(Room room) { if (room.ChangedAt == 0) room.ChangedAt = Now; }
        private bool Current(Connection c, out Room room, out Member member)
        {
            room = null; member = null;
            return c.Code != null && _rooms.TryGetValue(c.Code, out room) && room.Members.TryGetValue(c.MemberId, out member) && ReferenceEquals(member.Connection, c);
        }
        private void Session(Room room, Member member, string requestId = null)
        {
            var proof = new ResumeProof { UserId = member.UserId, Code = room.Code, MemberId = member.Id, Token = member.Token, Host = member.Host, Visibility = room.Visibility, PublicId = room.PublicId, CreatedAt = room.CreatedAt, Expires = Now + (long)TimeSpan.FromDays(30).TotalMilliseconds };
            Send(member.Connection, "room:session", new { code = room.Code, memberId = member.Id, token = member.Token, host = member.Host, requestId, resumeKey = _protector.Protect(JsonSerializer.Serialize(proof, Json)) });
        }
        private void EnsureHost(Room room)
        {
            if (room.Members.Values.Any(x => x.Host)) return;
            var next = room.Members.Values.FirstOrDefault(x => x.Online) ?? room.Members.Values.FirstOrDefault();
            if (next != null) { next.Host = true; DirectoryChanged(room); if (next.Online) Session(room, next); }
        }
        private void Leave(Connection c, bool retain)
        {
            if (Current(c, out var room, out var member))
            {
                if (retain) { member.Online = false; member.IsTyping = false; member.OfflineSince = Now; member.Connection = null; }
                else room.Members.Remove(member.Id);
                if (room.Members.Count == 0) _rooms.Remove(room.Code); else { EnsureHost(room); Update(room); }
                DirectoryChanged(room);
            }
            c.Code = null; c.MemberId = null;
        }
        private static void Identity(Member member, JsonElement payload, string[] owned, string name)
        {
            member.Name = string.IsNullOrWhiteSpace(name) ? "會員" : name;
            if (Get(payload, "equippedItems").ValueKind == JsonValueKind.Array) member.Items = Items(payload, owned);
            var layout = Get(payload, "baseLayout"); member.Layout = Layout(layout.ValueKind == JsonValueKind.Object ? layout : member.Layout, member.Items);
            if (Get(payload, "avatar").ValueKind != JsonValueKind.Undefined) member.Avatar = Avatar(Text(payload, "avatar"));
            if (Get(payload, "listening").ValueKind != JsonValueKind.Undefined) member.Listening = Bool(payload, "listening");
            var status = Text(payload, "status"); if (status != null) member.Status = Statuses.Contains(status) ? status : "focus";
        }
        private void Attach(Connection c, Room room, Member member, JsonElement payload, string[] owned)
        {
            if (member.Connection != null && !ReferenceEquals(member.Connection, c))
            {
                var old = member.Connection; old.Retired = true; old.Code = null; old.MemberId = null;
                Error(old, "session_replaced", "此會員已在另一個視窗連線。"); old.Outbox.Writer.TryComplete();
            }
            Identity(member, payload, owned, c.Name); member.Connection = c; member.Online = true; member.OfflineSince = 0;
            c.Code = room.Code; c.MemberId = member.Id; Session(room, member, c.RequestId); DirectoryChanged(room);
            var pictures = room.Members.Values.Select(Picture).Where(x => x != null).ToArray();
            if (pictures.Length != 0) Send(c, "room:avatars", pictures);
            if (Picture(member) is object picture) Broadcast(room, "room:avatars", new[] { picture });
            Update(room);
        }
        private void Join(Connection c, Room room, JsonElement payload, string[] owned, ResumeProof proof = null)
        {
            var member = room.Members.Values.FirstOrDefault(m => m.UserId == c.UserId);
            if (member == null && room.Members.Count >= MaxMembers) { Error(c, "room_full", "此房間已滿（最多 12 人）。"); return; }
            // One member account controls one room identity; moving rooms replaces its previous connection too.
            foreach (var other in _rooms.Values.ToArray())
                foreach (var prior in other.Members.Values.Where(m => m.UserId == c.UserId && !ReferenceEquals(m, member)).ToArray())
                {
                    if (prior.Connection != null && !ReferenceEquals(prior.Connection, c)) { prior.Connection.Retired = true; Error(prior.Connection, "session_replaced", "此會員已在另一個視窗連線。"); prior.Connection.Outbox.Writer.TryComplete(); }
                    other.Members.Remove(prior.Id); if (other.Members.Count == 0) _rooms.Remove(other.Code); else { EnsureHost(other); Update(other); } DirectoryChanged(other);
                }
            if (member == null)
            {
                var seats = room.Members.Values.Select(m => m.Seat).ToHashSet(); var wanted = Seat(payload);
                member = new Member { UserId = c.UserId, Id = proof?.MemberId ?? RandomHex(6), Token = proof?.Token ?? RandomHex(16), Name = "會員", JoinedAt = Now, Host = room.Members.Count == 0,
                    Seat = wanted.HasValue && !seats.Contains(wanted.Value) ? wanted.Value : Enumerable.Range(0, MaxMembers).First(n => !seats.Contains(n)) };
                room.Members.Add(member.Id, member);
            }
            Attach(c, room, member, payload, owned);
        }
        private ResumeProof Proof(Connection c, JsonElement payload)
        {
            try
            {
                var proof = JsonSerializer.Deserialize<ResumeProof>(_protector.Unprotect(Text(payload, "resumeKey") ?? ""), Json);
                return proof != null && proof.Expires > Now && proof.UserId == c.UserId && proof.Code == Text(payload, "code")?.Trim().ToUpperInvariant() && proof.MemberId == Text(payload, "memberId") && proof.Token == Text(payload, "token") ? proof : null;
            }
            catch (Exception error) when (error is CryptographicException || error is JsonException || error is ArgumentException) { return null; }
        }
        private void Dispatch(Connection c, string name, JsonElement payload, string[] owned)
        {
            c.RequestId = name == "room:create" || name == "room:join" || name == "room:join-public" || name == "room:current" || name == "room:resume" ? Clean(Text(payload, "requestId"), 80, null) : null;
            if (name == "rooms:subscribe") { Subscribe(c); return; }
            if (name == "rooms:unsubscribe") { c.DirectorySubscribed = false; return; }
            if (name == "room:current")
            {
                var room = _rooms.Values.FirstOrDefault(r => r.Members.Values.Any(m => m.UserId == c.UserId));
                if (room == null) Send(c, "room:current-result", new { requestId = c.RequestId, found = false });
                else Join(c, room, payload, owned);
                return;
            }
            if (name == "room:join-public")
            {
                var publicId = Text(payload, "publicId");
                var room = _rooms.Values.FirstOrDefault(r => r.Visibility == "public" && r.PublicId == publicId && r.Members.Values.Any(m => m.Online));
                if (room == null) { Error(c, "room_missing", "房間已關閉，請重新選擇。"); return; }
                Join(c, room, payload, owned); return;
            }
            if (name == "ping") { Send(c, "pong", new { serverNow = Now }); return; }
            if (name == "room:create")
            {
                var visibility = Get(payload, "visibility").ValueKind == JsonValueKind.Undefined ? "private" : Text(payload, "visibility");
                if (visibility != "public" && visibility != "private") { Error(c, "invalid_visibility", "請選擇公開或私人房間。"); return; }
                if (Get(payload, "title").ValueKind != JsonValueKind.Undefined && Clean(Text(payload, "title"), 32).Length == 0)
                { Error(c, "invalid_title", "請輸入房間名稱。"); return; }
                string code; do { code = RandomHex(3).ToUpperInvariant(); } while (_rooms.ContainsKey(code));
                var room = new Room { Code = code, Title = Clean(Text(payload, "title"), 32, "悠咪電競自習室"), Visibility = visibility };
                _rooms.Add(code, room); Join(c, room, payload, owned); return;
            }
            if (name == "room:join" || name == "room:resume")
            {
                var code = Text(payload, "code")?.Trim().ToUpperInvariant();
                if (code == null || !Regex.IsMatch(code, "^[0-9A-F]{6}$")) { Error(c, "invalid_code", "請輸入 6 碼邀請碼。"); return; }
                var proof = name == "room:resume" ? Proof(c, payload) : null;
                if (name == "room:resume" && proof == null) { Error(c, "invalid_session", "無法恢復此會員的房間，請重新加入。"); return; }
                if (!_rooms.TryGetValue(code, out var room))
                {
                    if (proof?.Host != true) { Error(c, "room_missing", "房間尚未恢復，正在等候房主重新連線。"); return; }
                    room = new Room { Code = code, Title = Clean(Text(payload, "title"), 32, "悠咪電競自習室"), Visibility = proof.Visibility == "public" ? "public" : "private",
                        PublicId = Regex.IsMatch(proof.PublicId ?? "", "^[0-9a-f]{32}$") ? proof.PublicId : RandomHex(16), CreatedAt = proof.CreatedAt > 0 ? proof.CreatedAt : Now }; _rooms.Add(code, room);
                }
                if (proof != null && room.Members.TryGetValue(proof.MemberId, out var existing) && (existing.UserId != c.UserId || existing.Token != proof.Token))
                { Error(c, "invalid_session", "房間憑證無效，請重新加入。"); return; }
                Join(c, room, payload, owned, proof); return;
            }
            if (name == "room:leave") { Leave(c, false); return; }
            if (!Current(c, out var current, out var self)) return;
            switch (name)
            {
                case "seat:take":
                    var seat = Seat(payload); if (!seat.HasValue) return;
                    if (current.Members.Values.Any(m => m.Id != self.Id && m.Seat == seat)) { Error(c, "seat_taken", "這個座位已經有人了。"); return; }
                    self.Seat = seat.Value; Update(current); break;
                case "status:set": self.Status = Statuses.Contains(Text(payload, "status") ?? "") ? Text(payload, "status") : "focus"; Changed(current); break;
                case "identity:update":
                    var previous = self.Avatar; var previousName = self.Name; Identity(self, payload, owned, c.Name);
                    if (self.Host && previousName != self.Name) DirectoryChanged(current);
                    if (previous != self.Avatar && Picture(self) is object picture) Broadcast(current, "room:avatars", new[] { picture }); Changed(current); break;
                case "keyboard:activity":
                    if (Now - c.LastKeyboard < 120) return; c.LastKeyboard = Now;
                    self.KeyCount = Math.Min(999999, self.KeyCount + (int)Number(payload, "count", 0, 0, 30));
                    self.IsTyping = Bool(payload, "isTyping"); if (self.IsTyping) self.LastKeyAt = Now; Changed(current); break;
                case "chat:send":
                    if (Now - c.LastChat < 350) return; c.LastChat = Now; var text = Clean(Text(payload, "text"), 280); if (text.Length == 0) return;
                    Broadcast(current, "chat:message", new { id = self.Id + "-" + Now, name = self.Name, text, at = Now }); break;
            }
        }
        private void Sweep()
        {
            lock (_gate)
            {
                if (_disposed) return;
                var now = Now;
                foreach (var room in _rooms.Values.ToArray())
                {
                    foreach (var member in room.Members.Values.ToArray())
                    {
                        if (!member.Online && now - member.OfflineSince > ResumeGraceMs) { room.Members.Remove(member.Id); Changed(room); DirectoryChanged(room); }
                        else if (member.IsTyping && now - member.LastKeyAt > 900) { member.IsTyping = false; Changed(room); }
                    }
                    if (room.Members.Count == 0) { _rooms.Remove(room.Code); continue; }
                    EnsureHost(room); if (room.ChangedAt != 0 && now - room.ChangedAt >= 250) Update(room);
                }
                FlushDirectory();
                if (now - _presenceAt >= 15000) { _presenceAt = now; foreach (var c in _connections) Send(c, "presence", new { online = _connections.Count }); }
            }
        }
        private static async Task Pump(Connection c, CancellationToken cancellation)
        {
            try
            {
                await foreach (var batch in c.Outbox.Reader.ReadAllAsync(cancellation))
                foreach (var frame in batch)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await c.Socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text, true, timeout.Token);
                }
                if (c.Socket.State == WebSocketState.Open || c.Socket.State == WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await c.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Room connection closed", timeout.Token);
                }
            }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException || error is IOException) { c.Socket.Abort(); }
        }
        public async Task HandleAsync(HttpContext context, int memberId, Func<Task<bool>> validateSession, Func<Task<string[]>> ownedItems, Func<Task<string>> memberName)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var c = new Connection { UserId = memberId, Socket = socket };
            lock (_gate) { if (_disposed) return; _connections.Add(c); Send(c, "presence", new { online = _connections.Count }); Send(c, "rooms:capabilities", new { version = 1 }); }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var pump = Pump(c, cancellation.Token); var validatedAt = Now;
            try
            {
                var buffer = new byte[8192];
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        if (received.MessageType == WebSocketMessageType.Close) return;
                        if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > MaxFrameBytes) { Error(c, "invalid_frame", "房間封包格式或大小不正確。"); return; }
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    using var document = JsonDocument.Parse(message.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
                    var name = Text(document.RootElement, "event"); var payload = Get(document.RootElement, "payload");
                    c.RequestId = Clean(Text(payload, "requestId"), 80, null);
                    if (Now - validatedAt >= 30000) { if (!await validateSession()) { Error(c, "auth_expired", "登入已失效，請重新登入。"); return; } validatedAt = Now; }
                    if (name == null || (payload.ValueKind != JsonValueKind.Object && payload.ValueKind != JsonValueKind.Undefined)) { Error(c, "invalid_frame", "房間封包格式不正確。"); return; }
                    if (Now - c.FrameWindow >= 1000) { c.FrameWindow = Now; c.FrameCount = 0; }
                    if (++c.FrameCount > 30) { Error(c, "rate_limit", "房間操作過於頻繁。"); return; }
                    var identity = name == "identity:update" || name == "room:create" || name == "room:join" || name == "room:resume" || name == "room:join-public" || name == "room:current";
                    if (identity)
                    {
                        if (Now - c.IdentityWindow >= 10000) { c.IdentityWindow = Now; c.IdentityCount = 0; }
                        if (++c.IdentityCount > 20) { Error(c, "rate_limit", "房間操作過於頻繁，請稍後重試。"); continue; }
                    }
                    var inventory = identity ? await ownedItems() : Array.Empty<string>();
                    if (identity) c.Name = await memberName();
                    lock (_gate) { if (_disposed || c.Retired) return; Dispatch(c, name, payload, inventory); }
                }
            }
            catch (JsonException) { Error(c, "invalid_frame", "房間封包格式不正確。"); }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException || error is IOException) { }
            catch (Exception) { Error(c, "service_unavailable", "房間服務暫時無法使用，請稍後重試。"); }
            finally
            {
                lock (_gate) { Leave(c, true); _connections.Remove(c); }
                c.Outbox.Writer.TryComplete(); await pump; cancellation.Cancel();
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return; _disposed = true; _timer.Dispose();
                foreach (var c in _connections) { c.Outbox.Writer.TryComplete(); c.Socket.Abort(); }
                _rooms.Clear();
            }
        }
    }
    internal static class DesktopRoomJson
    {
        internal static bool TryGetInt32Safe(this JsonElement value, out int number)
        { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }
    }
}
