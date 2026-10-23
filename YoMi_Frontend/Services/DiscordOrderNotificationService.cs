using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Orders;

namespace YoMi_Frontend.Services
{
    public class DiscordOrderNotificationService
    {
        // No automatic HTTP logging: a Discord webhook URL contains its credential.
        private static readonly HttpClient Sender = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        private readonly DBContext _db;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DiscordOrderNotificationService> _logger;
        private readonly HttpClient _http;

        public DiscordOrderNotificationService(DBContext db, IConfiguration configuration,
            ILogger<DiscordOrderNotificationService> logger, HttpClient http = null)
        { _db = db; _configuration = configuration; _logger = logger; _http = http ?? Sender; }

        public async Task ProcessAsync(CancellationToken cancellationToken = default)
        {
            var settings = _configuration.GetSection("DiscordOrders");
            if (!settings.GetValue<bool>("Enabled")) return;
            if (!Uri.TryCreate(settings["AdminBaseUrl"], UriKind.Absolute, out var admin) ||
                (admin.Scheme != "https" && admin.Scheme != "http") || admin.UserInfo.Length > 0 || admin.Query.Length > 0 || admin.Fragment.Length > 0)
                throw new InvalidOperationException("DiscordOrders:AdminBaseUrl 必須是完整的後台網址。");
            var hooks = new Dictionary<int, Uri>();
            foreach (var entry in new[] { (Kind: 0, Key: "CustomerServiceWebhook"), (Kind: 1, Key: "EmployeeWebhook") })
            {
                var value = settings[entry.Key];
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "discord.com" ||
                    !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                    !Regex.IsMatch(uri.AbsolutePath, @"^/api/(v\d+/)?webhooks/[0-9]{17,20}/[A-Za-z0-9_-]{20,200}$"))
                    throw new InvalidOperationException("DiscordOrders:" + entry.Key + " 必須是 Discord 官方 Webhook 網址。");
                hooks.Add(entry.Kind, uri);
            }
            if (hooks.Count == 0) return;
            var connection = _db.Database.GetDbConnection();
            var configuredHooks = hooks.ToArray();
            foreach (var hook in configuredHooks)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hook.Value.AbsoluteUri)));
                var paused = await connection.QuerySingleAsync<int>(@"
                    SELECT COUNT(*) FROM DiscordOrderNotification WHERE FinishedAtUtc IS NULL AND
                        (LastError IN @Blocked OR (NextAttemptAtUtc>SYSUTCDATETIME() AND
                            (LastError='HTTP 429 global' OR (LastError='HTTP 429' AND Kind IN @Kinds))))",
                    new { Blocked = new[] { "Blocked:401:" + hash, "Blocked:403:" + hash, "Blocked:404:" + hash },
                        Kinds = configuredHooks.Where(x => x.Value == hook.Value).Select(x => x.Key).ToArray() });
                if (paused > 0) hooks.Remove(hook.Key);
            }
            if (hooks.Count == 0) return;
            // A replacement webhook releases notifications blocked by the old credential.
            await connection.ExecuteAsync(@"
                UPDATE DiscordOrderNotification SET LastError=NULL, NextAttemptAtUtc=SYSUTCDATETIME()
                WHERE FinishedAtUtc IS NULL AND LastError LIKE 'Blocked:%' AND Kind IN @Kinds", new { Kinds = hooks.Keys.ToArray() });
            for (var i = 0; i < 10; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Claim one at a time, so the HTTP timeout is shorter than the recoverable two-minute lease.
                var item = await connection.QueryFirstOrDefaultAsync<Notification>(new CommandDefinition(@"
                    ;WITH nextItem AS (
                        SELECT TOP (1) * FROM DiscordOrderNotification WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                        WHERE FinishedAtUtc IS NULL AND NextAttemptAtUtc<=SYSUTCDATETIME() AND Kind IN @Kinds
                        ORDER BY NextAttemptAtUtc, CreatedAtUtc, OrderId, Kind
                    )
                    UPDATE nextItem SET LeaseId=@Lease, Attempts=Attempts+1, NextAttemptAtUtc=DATEADD(minute,2,SYSUTCDATETIME())
                    OUTPUT inserted.OrderId, inserted.Kind, inserted.LeaseId, inserted.Attempts;",
                    new { Kinds = hooks.Keys.ToArray(), Lease = Guid.NewGuid() }, cancellationToken: cancellationToken));
                if (item == null) return;
                var order = await _db.CustomerOrder.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.OrderId, cancellationToken);
                if (order == null || order.Status == CustomerOrderStatus.Cancelled ||
                    item.Kind == 0 && order.Status >= CustomerOrderStatus.InProgress ||
                    item.Kind == 1 && (!order.PaidAt.HasValue || order.Status != CustomerOrderStatus.Recruiting))
                {
                    await FinishAsync(item, null, "Skipped: order no longer actionable");
                    continue;
                }
                var retrySeconds = Math.Min(3600, 30 * Math.Pow(2, Math.Min(item.Attempts - 1, 7)));
                var error = "Invalid Discord response";
                try
                {
                    var payload = new
                    {
                        allowed_mentions = new { parse = Array.Empty<string>() },
                        embeds = new[] { new
                        {
                            title = item.Kind == 0 ? "新訂單通知" : "派單中｜開放接單報名",
                            description = OrderRules.Number(order),
                            color = item.Kind == 0 ? 3447003 : 3066993,
                            fields = new[] {
                                new { name = "服務", value = Plain(order.ServiceName), inline = false },
                                new { name = "伺服器", value = Plain(order.GameServer), inline = true },
                                new { name = "數量", value = order.Quantity + " " + Plain(order.Unit), inline = true },
                                new { name = item.Kind == 0 ? "金額" : "狀態", value = item.Kind == 0 ?
                                    (order.PayableAmount.HasValue ? "NT$ " + order.PayableAmount.Value.ToString("N0", CultureInfo.InvariantCulture) : "待報價") : "派單中", inline = true },
                                new { name = "操作", value = "[" + (item.Kind == 0 ? "查看訂單" : "前往接單") + "](" +
                                    admin.AbsoluteUri.TrimEnd('/') + (item.Kind == 0 ? "/CustomerOrders/Detail/" : "/EmployeeDispatch/Detail/") + order.Id + ")", inline = false }
                            }
                        } }
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post, hooks[item.Kind].AbsoluteUri + "?wait=true")
                    { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var response = await _http.SendAsync(request, timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                        if (body.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                            Regex.IsMatch(id.GetString(), @"^[0-9]{1,32}$"))
                        {
                            await FinishAsync(item, id.GetString(), null);
                            continue;
                        }
                    }
                    else error = "HTTP " + (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.NotFound)
                        error = "Blocked:" + (int)response.StatusCode + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hooks[item.Kind].AbsoluteUri)));
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        retrySeconds = Math.Max(retrySeconds, response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0);
                        var global = false;
                        try
                        {
                            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                            if (body.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var seconds) && double.IsFinite(seconds))
                                retrySeconds = Math.Max(retrySeconds, Math.Clamp(seconds, 1, 86400));
                            global = body.RootElement.TryGetProperty("global", out var flag) && flag.ValueKind == JsonValueKind.True;
                        }
                        catch (JsonException) { }
                        if (global) error = "HTTP 429 global";
                        var kinds = hooks.Where(x => global || x.Value == hooks[item.Kind]).Select(x => x.Key).ToArray();
                        await connection.ExecuteAsync(@"
                            UPDATE DiscordOrderNotification SET NextAttemptAtUtc=@Next
                            WHERE FinishedAtUtc IS NULL AND Kind IN @Kinds AND NextAttemptAtUtc<@Next",
                            new { Next = DateTime.UtcNow.AddSeconds(retrySeconds), Kinds = kinds });
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { error = ex.GetType().Name; }
                // shortcut: a crash or lost response after Discord accepts a message can cause a retry duplicate; delivery is at least once.
                await connection.ExecuteAsync(@"
                    UPDATE DiscordOrderNotification SET NextAttemptAtUtc=@Next, LastError=@Error, LeaseId=NULL
                    WHERE OrderId=@OrderId AND Kind=@Kind AND LeaseId=@LeaseId AND FinishedAtUtc IS NULL",
                    new { item.OrderId, item.Kind, item.LeaseId, Next = DateTime.UtcNow.AddSeconds(retrySeconds), Error = error });
                _logger.LogWarning("Discord notification for {OrderId} ({Kind}) deferred: {Reason}", item.OrderId, item.Kind, error);
                return;
            }
        }

        private Task FinishAsync(Notification item, string messageId, string error) => _db.Database.GetDbConnection().ExecuteAsync(@"
            UPDATE DiscordOrderNotification SET FinishedAtUtc=SYSUTCDATETIME(), MessageId=@MessageId, LastError=@Error, LeaseId=NULL
            WHERE OrderId=@OrderId AND Kind=@Kind AND LeaseId=@LeaseId AND FinishedAtUtc IS NULL",
            new { item.OrderId, item.Kind, item.LeaseId, MessageId = messageId, Error = error });

        private static string Plain(string value) => Regex.Replace(value ?? "", @"([\\`*_{}\[\]()<>~|])", @"\$1")
            .Replace("@", "@\u200b").Replace("\r", " ").Replace("\n", " ");

        private sealed class Notification
        {
            public Guid OrderId { get; set; }
            public int Kind { get; set; }
            public Guid LeaseId { get; set; }
            public int Attempts { get; set; }
        }
    }
}
