using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;

var dbFile = Path.Combine(Path.GetTempPath(), $"yomi-community-{Guid.NewGuid():N}.sqlite");
var keys = new EphemeralDataProtectionProvider();
WebApplication app = null;
var clients = new List<ClientWebSocket>();
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
async Task<WebApplication> Start()
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), EnvironmentName = "Development" });
    builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton<IDataProtectionProvider>(keys);
    builder.Services.AddDbContext<DBContext>(x => x.UseSqlite("Data Source=" + dbFile));
    builder.Services.AddScoped<DesktopPetStore>();
    foreach (var name in new[] { "DesktopAnnouncements", "DesktopRooms" })
    {
        var type = typeof(DesktopPetStore).Assembly.GetType("YoMi_Frontend.Services." + name);
        if (type != null) { if (name == "DesktopRooms") builder.Services.AddSingleton(type); else builder.Services.AddScoped(type); }
    }
    builder.Services.AddControllers().AddApplicationPart(typeof(DesktopPetStore).Assembly);
    var host = builder.Build(); host.Use(async (ctx, next) => { ctx.Request.Scheme = "https"; await next(); });
    host.UseWebSockets(); host.MapControllers(); await host.StartAsync(); return host;
}
string Url() => app.Urls.Single();
async Task<JsonElement> Api(string method, string path, object body, int? member, int expected = 200)
{
    using var http = new HttpClient(); using var request = new HttpRequestMessage(new HttpMethod(method), Url() + "/api/desktop/" + path);
    if (member.HasValue) request.Headers.Authorization = new("Bearer", "fixture-" + member);
    if (body != null) request.Content = JsonContent.Create(body);
    using var response = await http.SendAsync(request); var text = await response.Content.ReadAsStringAsync();
    Check((int)response.StatusCode == expected, $"{method} {path}: expected {expected}, got {(int)response.StatusCode} {text[..Math.Min(text.Length, 100)]}");
    return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
}
async Task<ClientWebSocket> Connect(int member)
{
    var client = new ClientWebSocket(); client.Options.SetRequestHeader("Authorization", "Bearer fixture-" + member);
    await client.ConnectAsync(new Uri(Url().Replace("http:", "ws:") + "/api/desktop/rooms"), CancellationToken.None);
    clients.Add(client); return client;
}
async Task Send(ClientWebSocket client, string name, object payload)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { @event = name, payload });
    await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}
async Task<JsonElement> Receive(ClientWebSocket client, string name, Func<JsonElement, bool> match = null)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (true)
    {
        using var message = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
        do { result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Closed waiting for " + name); message.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        var frame = JsonDocument.Parse(message.ToArray()).RootElement;
        if (frame.GetProperty("event").GetString() == name)
        { var payload = frame.GetProperty("payload").Clone(); if (match == null || match(payload)) return payload; }
    }
}
try
{
    app = await Start();
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<DBContext>(); await db.Database.EnsureCreatedAsync();
        db.FrontendMember.AddRange(new FrontendMember { Id = 1, Name = "Admin", Role = "admin" }, new FrontendMember { Id = 2, Name = "Member", Role = "user" });
        db.FrontendSiteSetting.Add(new FrontendSiteSetting { Key = "unrelated", Value = "keep", Label = "Other", Description = "", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
    var list = await Api("GET", "announcements", null, null);
    Check(!list.GetProperty("canEdit").GetBoolean() && list.GetProperty("announcements").GetArrayLength() == 0, "anonymous public empty list");
    await Api("POST", "announcements", new { title = "One", body = "Body", revision = 0 }, null, 401);
    await Api("POST", "announcements", new { title = "One", body = "Body", revision = 0 }, 2, 403);
    await Api("POST", "announcements", new { title = new string('x', 81), body = "Body", revision = 0 }, 1, 400);
    list = await Api("POST", "announcements", new { title = "One", body = "Body", pinned = true, revision = 0, author = "forged" }, 1);
    var post = list.GetProperty("announcements")[0]; var id = post.GetProperty("id").GetInt64();
    Check(post.GetProperty("author").GetString() == "Admin" && list.GetProperty("revision").GetInt64() == 1, "server author and revision");
    await Api("PUT", "announcements/" + id, new { title = "Stale", body = "Body", revision = 0 }, 1, 409);
    list = await Api("PUT", "announcements/" + id, new { title = "Two", body = "Updated", revision = 1 }, 1);
    Check(list.GetProperty("announcements")[0].GetProperty("title").GetString() == "Two", "admin update");
    await Api("DELETE", "announcements/" + id, new { revision = 1 }, 1, 409);
    list = await Api("DELETE", "announcements/" + id, new { revision = 2 }, 1);
    Check(list.GetProperty("announcements").GetArrayLength() == 0 && list.GetProperty("revision").GetInt64() == 3, "admin delete");
    using (var scope = app.Services.CreateScope()) Check((await scope.ServiceProvider.GetRequiredService<DBContext>().FrontendSiteSetting.SingleAsync(x => x.Key == "unrelated")).Value == "keep", "unrelated settings retained");
    using (var unauth = new ClientWebSocket())
    {
        try { await unauth.ConnectAsync(new Uri(Url().Replace("http:", "ws:") + "/api/desktop/rooms"), CancellationToken.None); Check(false, "anonymous room denied"); }
        catch (WebSocketException) { Check(true, "anonymous room denied"); }
    }
    var host = await Connect(1); var guest = await Connect(2);
    await Send(host, "room:create", new { displayName = "Host", title = "Study", equippedItems = new[] { "hw-cat-ears", "idle:face-happy", "background-beach", "decor-plant" }, baseLayout = new { placements = new[] { new { id = "decor-plant", x = .5, y = .8, scale = 1.2, flip = true, layer = "back" } } }, avatar = "preset:face-happy", listening = true });
    var hs = await Receive(host, "room:session"); var code = hs.GetProperty("code").GetString();
    await Send(guest, "room:join", new { code, displayName = "Guest" }); var gs = await Receive(guest, "room:session");
    var room = await Receive(guest, "room:update", x => x.GetProperty("members").GetArrayLength() == 2);
    var h = room.GetProperty("members")[0];
    Check(DesktopPetStore.ActiveConnections == 0, "idle websocket does not retain a database connection");
    Check(h.GetProperty("listening").GetBoolean() && h.GetProperty("avatarKey").GetString() == "preset:face-happy" && h.GetProperty("baseLayout").GetProperty("theme").GetString() == "background-beach", "music/avatar/expanded scene preserved");
    Check(h.GetProperty("baseLayout").GetProperty("placements")[0].GetProperty("x").GetDouble() == .5, "scene coordinates preserved");
    await Send(host, "identity:update", new { equippedItems = new[] { "hw-cat-ears", "outfit-maid", "background-beach", "decor-seashell", "../unsafe" }, baseLayout = new { placements = new[] { new { id = "decor-seashell", x = 999, y = -9, scale = 99, layer = "unsafe" } } }, avatar = "data:image/svg+xml;base64,PHN2Zz4=" });
    room = await Receive(host, "room:update", x => x.GetProperty("members")[0].GetProperty("equippedItems").EnumerateArray().Any(v => v.GetString() == "decor-seashell"));
    h = room.GetProperty("members")[0];
    Check(!h.GetProperty("equippedItems").GetRawText().Contains("outfit-maid") && !h.GetProperty("equippedItems").GetRawText().Contains("unsafe"), "unowned and unknown equipment rejected");
    var placement = h.GetProperty("baseLayout").GetProperty("placements")[0];
    Check(placement.GetProperty("x").GetDouble() == .96 && placement.GetProperty("y").GetDouble() == .12 && placement.GetProperty("scale").GetDouble() == 1.8 && placement.GetProperty("layer").GetString() == "front", "scene bounds clamped");
    Check(h.GetProperty("avatarKey").ValueKind == JsonValueKind.Null, "executable avatar type rejected");
    const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
    await Send(host, "identity:update", new { avatar = png });
    var pictures = await Receive(guest, "room:avatars"); Check(pictures[0].GetProperty("avatar").GetString() == png && pictures[0].GetProperty("key").GetString().StartsWith("img:"), "custom avatar delivered separately");
    await Send(guest, "seat:take", new { seat = 0 }); Check((await Receive(guest, "room:error")).GetProperty("code").GetString() == "seat_taken", "occupied seat rejected");
    await Send(guest, "seat:take", new { seat = 5 });
    room = await Receive(guest, "room:update", x => x.GetProperty("members")[1].GetProperty("seat").GetInt32() == 5);
    Check(room.GetProperty("members")[1].GetProperty("seat").GetInt32() == 5, "free seat moved");
    await Send(guest, "keyboard:activity", new { count = 999, isTyping = true, key = "secret" });
    room = await Receive(guest, "room:update", x => x.GetProperty("members")[1].GetProperty("keyCount").GetInt32() == 30);
    Check(!room.GetRawText().Contains("secret"), "keyboard count clamped without key content");
    await Send(guest, "chat:send", new { text = "Hi <everyone>" });
    Check((await Receive(host, "chat:message")).GetProperty("text").GetString() == "Hi everyone", "chat delivered to second client");
    var attacker = await Connect(3); await Send(attacker, "room:resume", gs);
    Check((await Receive(attacker, "room:error")).GetProperty("code").GetString() == "invalid_session", "stolen room token denied for another account");
    DesktopPetStore.HoldNextMember = 2;
    await Send(guest, "room:create", new { title = "Delayed old request" });
    await DesktopPetStore.DocumentsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var replacement = await Connect(2); await Send(replacement, "room:resume", gs); await Receive(replacement, "room:session");
    Check((await Receive(guest, "room:error")).GetProperty("code").GetString() == "session_replaced", "same-member connection replaced explicitly");
    DesktopPetStore.DocumentsRelease.TrySetResult(true); await Task.Delay(100); guest.Abort();
    await Send(replacement, "status:set", new { status = "study" });
    room = await Receive(replacement, "room:update", x => x.GetProperty("members")[1].GetProperty("status").GetString() == "study");
    Check(room.GetProperty("members")[1].GetProperty("online").GetBoolean(), "old disconnect cannot detach replacement");
    replacement.Abort();
    room = await Receive(host, "room:update", x => x.GetProperty("members").GetArrayLength() == 2 && !x.GetProperty("members")[1].GetProperty("online").GetBoolean());
    Check(room.GetProperty("members")[1].GetProperty("seat").GetInt32() == 5, "disconnect reserves member seat");
    replacement = await Connect(2); await Send(replacement, "room:resume", gs); await Receive(replacement, "room:session");
    room = await Receive(replacement, "room:update", x => x.GetProperty("members")[1].GetProperty("online").GetBoolean());
    Check(room.GetProperty("members")[1].GetProperty("keyCount").GetInt32() == 30 && room.GetProperty("members")[1].GetProperty("seat").GetInt32() == 5, "resume preserves counters and seat");
    for (var member = 4; member <= 13; member++) { var c = await Connect(member); await Send(c, "room:join", new { code }); await Receive(c, "room:session"); }
    await Send(attacker, "room:join", new { code });
    Check((await Receive(attacker, "room:error")).GetProperty("code").GetString() == "room_full", "thirteenth member rejected");
    var malformed = await Connect(25); await Send(malformed, "room:join", new { code = "../bad" });
    Check((await Receive(malformed, "room:error")).GetProperty("code").GetString() == "invalid_code", "invalid room code rejected");
    await Send(malformed, "chat:send", new { text = new string('x', 33000) });
    Check((await Receive(malformed, "room:error")).GetProperty("code").GetString() == "invalid_frame", "oversized frame rejected");
    await Send(replacement, "room:leave", new { }); await Send(attacker, "room:join", new { code }); await Receive(attacker, "room:session"); Check(true, "leave releases seat");
    foreach (var c in clients) c.Abort(); clients.Clear(); await app.StopAsync(); await app.DisposeAsync(); app = await Start();
    guest = await Connect(2); await Send(guest, "room:resume", gs);
    Check((await Receive(guest, "room:error")).GetProperty("code").GetString() == "room_missing", "joiner cannot recreate missing room");
    host = await Connect(1); await Send(host, "room:resume", hs); var reopened = await Receive(host, "room:session");
    Check(reopened.GetProperty("code").GetString() == code, "authenticated host restores code after restart");
    await Send(guest, "room:resume", gs); await Receive(guest, "room:session");
    Check((await Receive(guest, "room:update", x => x.GetProperty("members").GetArrayLength() == 2)).GetProperty("members").GetArrayLength() == 2, "joiner resumes after host recreation");
    await Send(host, "room:leave", new { });
    var promoted = await Receive(guest, "room:session"); Check(promoted.GetProperty("host").GetBoolean(), "host leave transfers signed host role");
    await Send(host, "room:join", new { code }); await Receive(host, "room:session");
    host.Abort();
    room = await Receive(guest, "room:update", x => x.GetProperty("members").GetArrayLength() == 2 && x.GetProperty("members").EnumerateArray().Any(m => !m.GetProperty("online").GetBoolean()));
    var idle = await Connect(30); var revoked = await Connect(31); DesktopPetStore.Revoked.Add(31);
    using (var heartbeatStop = new CancellationTokenSource())
    {
        async Task Heartbeat(ClientWebSocket c)
        {
            try { while (!heartbeatStop.IsCancellationRequested) { await Task.Delay(15000, heartbeatStop.Token); await Send(c, "ping", new { }); } }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is InvalidOperationException || error is IOException) { }
        }
        var heartbeat = Heartbeat(guest); var revokedHeartbeat = Heartbeat(revoked);
        Console.WriteLine("Checking real 120-second resume grace, session revalidation, and idle cleanup...");
        await Task.Delay(32000);
        Check((await Receive(revoked, "room:error")).GetProperty("code").GetString() == "auth_expired", "revoked bearer closes active room connection");
        await Task.Delay(30000);
        try { await Receive(idle, "pong"); Check(false, "idle socket expired"); } catch (Exception error) when (error.Message.StartsWith("Closed waiting") || error is WebSocketException) { Check(true, "idle socket expired"); }
        await Task.Delay(60000);
        room = await Receive(guest, "room:update", x => x.GetProperty("members").GetArrayLength() == 1);
        Check(room.GetProperty("members")[0].GetProperty("online").GetBoolean(), "120-second expiry removes disconnected member while heartbeat keeps peer online");
        heartbeatStop.Cancel(); await Task.WhenAll(heartbeat, revokedHeartbeat);
    }
    list = await Api("GET", "announcements", null, null); Check(list.GetProperty("revision").GetInt64() == 3, "announcement revision survives restart");
    Console.WriteLine($"Community checks passed: {checks}");
}
finally
{
    foreach (var c in clients) { c.Abort(); c.Dispose(); }
    if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
    File.Delete(dbFile); File.Delete(dbFile + "-shm"); File.Delete(dbFile + "-wal");
}
