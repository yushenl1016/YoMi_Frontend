using System.Collections;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using YoMi_Frontend.Services;

// Exercise the real dispatcher and sweep with isolated connections and an ephemeral protection key.
// Reflection only controls time/transport; no production database or test hooks are used.
internal static class DirectoryChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static object Get(object value, string name) => value.GetType().GetField(name, Fields)!.GetValue(value)!;
    private static void Set(object value, string name, object field) => value.GetType().GetField(name, Fields)!.SetValue(value, field);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private sealed class Harness : IDisposable
    {
        internal readonly DesktopRooms Service;
        internal Harness(IDataProtectionProvider keys)
        {
            Service = new DesktopRooms(keys);
            ((Timer)Get(Service, "_timer")).Change(Timeout.Infinite, Timeout.Infinite);
        }
        internal object Connect(int id)
        {
            var c = Activator.CreateInstance(typeof(DesktopRooms).GetNestedType("Connection", BindingFlags.NonPublic)!)!;
            Set(c, "UserId", id); Set(c, "Name", "Member " + id); Set(c, "Socket", new SinkSocket());
            var connections = Get(Service, "_connections"); connections.GetType().GetMethod("Add")!.Invoke(connections, new[] { c });
            return c;
        }
        internal void Call(string name, params object[] args)
        { lock (Get(Service, "_gate")) typeof(DesktopRooms).GetMethod(name, Fields)!.Invoke(Service, args); }
        internal List<JsonElement> Send(object c, string name, object payload)
        { Call("Dispatch", c, name, Json(payload), Array.Empty<string>()); return Drain(c); }
        internal void Sweep() => Call("Sweep");
        internal object Room(string code) => ((IDictionary)Get(Service, "_rooms"))[code]!;
        internal static List<JsonElement> Drain(object c)
        {
            var box = Get(c, "Outbox"); var reader = box.GetType().GetProperty("Reader")!.GetValue(box)!;
            var method = reader.GetType().GetMethod("TryRead")!;
            var frames = new List<JsonElement>(); object[] args = { null! };
            while ((bool)method.Invoke(reader, args)!)
            {
                if (args[0] is byte[] one) frames.Add(JsonDocument.Parse(one).RootElement.Clone());
                else foreach (var frame in (IEnumerable<byte[]>)args[0]) frames.Add(JsonDocument.Parse(frame).RootElement.Clone());
            }
            return frames;
        }
        public void Dispose() => Service.Dispose();
    }
    private sealed class SinkSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => "";
        public override WebSocketState State => state;
        public override string SubProtocol => "";
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string d, CancellationToken t) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string d, CancellationToken t) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken t) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType m, bool e, CancellationToken t) => Task.CompletedTask;
    }
    private static JsonElement Event(List<JsonElement> frames, string name) => frames.Last(x => x.GetProperty("event").GetString() == name).GetProperty("payload");
    private static JsonElement[] Rows(List<JsonElement> frames) => frames.Where(x => x.GetProperty("event").GetString() == "rooms:snapshot").SelectMany(x => x.GetProperty("payload").GetProperty("rooms").EnumerateArray()).ToArray();
    internal static void Run(Action<bool, string> check)
    {
        var keys = new EphemeralDataProtectionProvider();
        using var h = new Harness(keys);
        var observer = h.Connect(100); var owner = h.Connect(1); var guest = h.Connect(2);
        var empty = h.Send(observer, "rooms:subscribe", new { });
        check(empty.Any(x => x.GetProperty("event").GetString() == "rooms:snapshot"), "directory subscription returns snapshot");
        check(Rows(empty).Length == 0 && Event(empty, "rooms:snapshot").GetProperty("last").GetBoolean(), "empty snapshot is complete");
        var legacy = h.Send(owner, "room:create", new { title = "Private legacy", requestId = "legacy" });
        var ps = Event(legacy, "room:session"); var privateCode = ps.GetProperty("code").GetString()!;
        var privateRoom = Event(legacy, "room:update");
        check(privateRoom.GetProperty("visibility").GetString() == "private" && ps.GetProperty("requestId").GetString() == "legacy", "legacy create defaults private and echoes request id");
        var privateId = privateRoom.GetProperty("publicId").GetString()!;
        h.Sweep(); check(!Harness.Drain(observer).Any(x => x.GetProperty("event").GetString()!.StartsWith("rooms:")), "private rooms never produce directory deltas");
        var denied = h.Send(guest, "room:join-public", new { publicId = privateId, requestId = "forged" });
        check(Event(denied, "room:error").GetProperty("code").GetString() == "room_missing", "private id cannot be used for public join");
        var joined = h.Send(guest, "room:join", new { code = privateCode, requestId = "invite" });
        check(Event(joined, "room:session").GetProperty("code").GetString() == privateCode, "private room remains joinable by invitation");
        var created = h.Send(owner, "room:create", new { title = "Public <Room>", visibility = "public", requestId = "create-public" });
        var session = Event(created, "room:session"); var code = session.GetProperty("code").GetString()!;
        var publicRoom = Event(created, "room:update"); var id = publicRoom.GetProperty("publicId").GetString()!;
        h.Sweep(); var delta = Event(Harness.Drain(observer), "rooms:changed");
        check(id != code && id.Length >= 24 && delta.GetProperty("upserts").GetArrayLength() == 1, "public create produces separate unpredictable directory id");
        var row = delta.GetProperty("upserts")[0];
        check(row.EnumerateObject().Select(x => x.Name).OrderBy(x => x).SequenceEqual(new[] { "capacity", "count", "createdAt", "hostName", "onlineCount", "publicId", "title" }), "directory exposes only allowed public fields");
        check(row.GetProperty("hostName").GetString() == "Member 1" && row.GetProperty("count").GetInt32() == 1, "directory uses authoritative host name and occupied count");
        var revision = delta.GetProperty("revision").GetInt64();
        h.Send(owner, "keyboard:activity", new { count = 1, isTyping = true }); h.Send(owner, "chat:send", new { text = "hi" }); h.Sweep();
        check(!Harness.Drain(observer).Any(x => x.GetProperty("event").GetString()!.StartsWith("rooms:")), "typing and chat do not mutate directory");
        var bad = h.Send(guest, "room:join-public", new { publicId = "missing", requestId = "missing" });
        check(Event(bad, "room:error").GetProperty("requestId").GetString() == "missing" && (string)Get(guest, "Code") == privateCode, "failed switch preserves original room and echoes request id");
        h.Send(guest, "room:join-public", new { publicId = id, requestId = "join-public" }); h.Sweep();
        delta = Event(Harness.Drain(observer), "rooms:changed");
        check(delta.GetProperty("revision").GetInt64() == revision + 1 && delta.GetProperty("upserts")[0].GetProperty("count").GetInt32() == 2, "public join updates count with next revision");
        h.Call("Leave", guest, true); h.Sweep(); delta = Event(Harness.Drain(observer), "rooms:changed");
        check(delta.GetProperty("upserts")[0].GetProperty("count").GetInt32() == 2 && delta.GetProperty("upserts")[0].GetProperty("onlineCount").GetInt32() == 1, "disconnect retains occupied seat but lowers online count");
        var reconnect = h.Connect(2); var current = h.Send(reconnect, "room:current", new { requestId = "lost-ack" });
        check(Event(current, "room:session").GetProperty("code").GetString() == code && Event(current, "room:session").GetProperty("requestId").GetString() == "lost-ack", "current query restores authenticated membership after lost response");
        var absent = h.Send(h.Connect(99), "room:current", new { requestId = "none" });
        check(!Event(absent, "room:current-result").GetProperty("found").GetBoolean(), "current query reports missing membership without creating room");
        for (var n = 3; n <= 11; n++) h.Send(h.Connect(n), "room:join-public", new { publicId = id });
        var challenger1 = h.Connect(12); var challenger2 = h.Connect(13);
        var own1 = Event(h.Send(challenger1, "room:create", new { }), "room:session");
        var own2 = Event(h.Send(challenger2, "room:create", new { }), "room:session");
        List<JsonElement> result1 = null!, result2 = null!;
        Parallel.Invoke(() => result1 = h.Send(challenger1, "room:join-public", new { publicId = id }), () => result2 = h.Send(challenger2, "room:join-public", new { publicId = id }));
        var loser = result1.Any(x => x.GetProperty("event").GetString() == "room:error") ? challenger1 : challenger2;
        var loserSession = ReferenceEquals(loser, challenger1) ? own1 : own2;
        check(((IDictionary)Get(h.Room(code), "Members")).Count == 12 && (string)Get(loser, "Code") == loserSession.GetProperty("code").GetString(), "last-seat race never overfills and failed contender keeps old room");
        h.Send(owner, "room:leave", new { }); h.Sweep(); delta = Event(Harness.Drain(observer), "rooms:changed");
        check(delta.GetProperty("upserts")[0].GetProperty("hostName").GetString() == "Member 2", "host transfer updates directory name");
        Set(reconnect, "Name", "Member <Two> & friends"); h.Send(reconnect, "identity:update", new { }); h.Sweep();
        check(Event(Harness.Drain(observer), "rooms:changed").GetProperty("upserts")[0].GetProperty("hostName").GetString() == "Member <Two> & friends", "host rename preserves literal display text");
        var invalid = h.Send(reconnect, "room:create", new { visibility = true, requestId = "bad-visibility" });
        check(invalid.Any(x => x.GetProperty("event").GetString() == "room:error") && (string)Get(reconnect, "Code") == code, "malformed visibility cannot silently create private room or leave current room");
        invalid = h.Send(reconnect, "room:create", new { title = "   ", visibility = "public", requestId = "blank-title" });
        check(Event(invalid, "room:error").GetProperty("code").GetString() == "invalid_title" && (string)Get(reconnect, "Code") == code, "blank title rejection retains current room");
        h.Send(observer, "rooms:unsubscribe", new { }); h.Send(reconnect, "room:leave", new { }); h.Sweep();
        check(!Harness.Drain(observer).Any(x => x.GetProperty("event").GetString()!.StartsWith("rooms:")), "unsubscribe stops directory events");

        using (var restored = new Harness(keys))
        {
            var c = restored.Connect(1);
            var resumed = restored.Send(c, "room:resume", session);
            var room = Event(resumed, "room:update");
            check(room.GetProperty("visibility").GetString() == "public" && room.GetProperty("publicId").GetString() == id, "protected proof restores public visibility and original directory id");
            var o = restored.Connect(100); var snapshot = Rows(restored.Send(o, "rooms:subscribe", new { }));
            check(snapshot[0].GetProperty("createdAt").GetInt64() == row.GetProperty("createdAt").GetInt64(), "restart preserves protected creation time");
            var priv = restored.Connect(1);
            var forged = JsonSerializer.Deserialize<Dictionary<string, object>>(ps.GetRawText())!; forged["visibility"] = "public"; forged["publicId"] = id;
            room = Event(restored.Send(priv, "room:resume", forged), "room:update");
            check(room.GetProperty("visibility").GetString() == "private", "unprotected payload cannot promote private proof to public");
        }
        using (var old = new Harness(keys))
        {
            var protector = keys.CreateProtector("YoMi.Desktop.RoomResume.v1");
            var proof = new { userId = 1, code = "ABC123", memberId = "old-member", token = "old-token", host = true, expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds() };
            var payload = new { proof.code, proof.memberId, proof.token, resumeKey = protector.Protect(JsonSerializer.Serialize(proof)), visibility = "public" };
            var room = Event(old.Send(old.Connect(1), "room:resume", payload), "room:update");
            check(room.GetProperty("visibility").GetString() == "private", "legacy protected proof restores as private");
        }
        using (var expiry = new Harness(keys))
        {
            var host = expiry.Connect(1); var peer = expiry.Connect(2); var watch = expiry.Connect(100);
            var fresh = Event(expiry.Send(host, "room:create", new { visibility = "public" }), "room:session");
            expiry.Send(peer, "room:join", new { code = fresh.GetProperty("code").GetString() }); expiry.Send(watch, "rooms:subscribe", new { });
            expiry.Call("Leave", host, true); expiry.Call("Leave", peer, true); expiry.Sweep();
            check(Event(Harness.Drain(watch), "rooms:changed").GetProperty("removed").GetArrayLength() == 1, "all-offline room disappears from directory");
            expiry.Send(host, "room:current", new { }); expiry.Sweep();
            check(Event(Harness.Drain(watch), "rooms:changed").GetProperty("upserts")[0].GetProperty("count").GetInt32() == 2, "reconnect restores directory row with reserved seats");
            var members = (IDictionary)Get(expiry.Room(fresh.GetProperty("code").GetString()!), "Members");
            foreach (var member in members.Values) if (!(bool)Get(member!, "Online")) Set(member!, "OfflineSince", 1L);
            expiry.Sweep();
            check(Event(Harness.Drain(watch), "rooms:changed").GetProperty("upserts")[0].GetProperty("count").GetInt32() == 1, "expired reservation updates directory count");
        }
        using (var bulk = new Harness(keys))
        {
            for (var n = 1; n <= 6700; n++) bulk.Send(bulk.Connect(n), "room:create", new { visibility = "public", title = new string('房', 32) });
            var watch = bulk.Connect(10000); var frames = bulk.Send(watch, "rooms:subscribe", new { });
            var snapshots = frames.Where(x => x.GetProperty("event").GetString() == "rooms:snapshot").ToArray();
            check(Rows(frames).Length == 6700 && snapshots.Length == 67 && ((WebSocket)Get(watch, "Socket")).State == WebSocketState.Open, "large snapshot passes 64-chunk queue boundary without losing rooms");
            check(snapshots.Sum(x => System.Text.Encoding.UTF8.GetByteCount(x.GetRawText())) > 512 * 1024 && snapshots.All(x => System.Text.Encoding.UTF8.GetByteCount(x.GetRawText()) < 512 * 1024), "large directory exceeds 512KiB total but every frame stays bounded");
            check(snapshots.Select((x, n) => x.GetProperty("payload").GetProperty("part").GetInt32() == n && x.GetProperty("payload").GetProperty("last").GetBoolean() == (n == 66)).All(x => x), "snapshot parts are ordered with one final part");
            bulk.Send(bulk.Connect(9000), "room:create", new { visibility = "public" }); bulk.Sweep();
            check(Event(Harness.Drain(watch), "rooms:changed").GetProperty("revision").GetInt64() == Event(frames, "rooms:snapshot").GetProperty("revision").GetInt64() + 1, "delta follows complete snapshot revision");
            for (var n = 0; n < 70; n++) bulk.Call("Dispatch", watch, "ping", Json(new { }), Array.Empty<string>());
            check(((WebSocket)Get(watch, "Socket")).State == WebSocketState.Aborted, "slow connection is disconnected when bounded outbox fills");
        }
    }
}
