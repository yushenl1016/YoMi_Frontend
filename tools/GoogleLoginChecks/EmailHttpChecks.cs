using Microsoft.AspNetCore.Builder;
using ImageMagick;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.RegularExpressions;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend;
using YoMi_Frontend.Controllers;
using YoMi_Frontend.Services;

static class EmailHttpChecks
{
    public static async Task RunAsync(bool preview = false)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "YoMi_Frontend", "YoMi_Frontend.csproj"))) repo = repo.Parent;
        var root = Path.Combine(repo?.FullName ?? throw new DirectoryNotFoundException("Frontend repository not found."), "YoMi_Frontend");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AccountController).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Testing"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(preview ? "http://127.0.0.1:5199" : "http://127.0.0.1:0");
        // Exercise real Startup authentication with empty config, without reading production credentials or starting background work.
        new Startup(new ConfigurationBuilder().Build(), builder.Environment).ConfigureServices(builder.Services);
        builder.Services.RemoveAll<DBContext>();
        builder.Services.RemoveAll<DbContextOptions<DBContext>>();
        builder.Services.AddScoped<DBContext>(_ => new EmailMoneyCheckDbContext(new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options));
        var worker = builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(DiscordOrderNotificationHostedService));
        builder.Services.Remove(worker);
        await using var app = builder.Build();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DBContext>();
            await db.Database.EnsureCreatedAsync();
            db.FrontendMember.Add(new FrontendMember { Id = 77, Email = "legacy@example.test", Name = "Legacy" });
            db.FrontendMember.Add(new FrontendMember { Id = 78, Email = "flagged@example.test", Name = "Flagged", HasLoggedInNewSite = true });
            db.FrontendMember.Add(new FrontendMember { Id = 79, Email = "pending@example.test", Name = "Pending", EmailPasswordHash = "imported-hash", PasswordHash = "legacy-sub" });
            db.FrontendConsumptionRecord.AddRange(
                new FrontendConsumptionRecord { UserId = 77, Amount = 6000m, CountsForVip = true, CreatedAt = DateTime.UtcNow },
                new FrontendConsumptionRecord { UserId = 77, Amount = 99999m, CountsForVip = false },
                new FrontendConsumptionRecord { UserId = 77, Amount = 99999m, CountsForVip = true, IsReversed = true });
            await db.SaveChangesAsync();
            await new EmailMemberLogin(db).RegisterAsync("password@example.test", "Password member", "Password123!");
        }
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single();
        if (preview)
        {
            Console.WriteLine("Isolated login preview: " + address + "/email-auth");
            await app.WaitForShutdownAsync();
            return;
        }
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
        await BackgroundHttpChecks.RunAsync(app, client);
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        string TransferToken(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"TransferToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        async Task<string> Page()
        {
            var response = await client.GetAsync("/email-auth?returnUrl=%2Forders%2Fnew&ref=0123456789ABCDEF");
            Check(response.StatusCode == HttpStatusCode.OK, "Login page failed: " + await response.Content.ReadAsStringAsync());
            return await response.Content.ReadAsStringAsync();
        }
        async Task<HttpResponseMessage> Post(string route, string html, params (string, string)[] values)
        {
            var data = values.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();
            data.Add(new("__RequestVerificationToken", Token(html)));
            return await client.PostAsync(route, new FormUrlEncodedContent(data));
        }
        var start = await Page();
        Check(start.Contains("id=\"auth-email\"") && !start.Contains("type=\"password\""), "First screen must ask only for Email.");
        var noCsrf = await client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Check(noCsrf.StatusCode == HttpStatusCode.BadRequest, "Registration requires CSRF.");
        foreach (var route in new[] { "/account/transfer", "/account/transfer/confirm", "/account/transfer/support/start", "/account/transfer/complete", "/account/transfer/support" })
            Check((await client.PostAsync(route, new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode == HttpStatusCode.BadRequest, route + " requires CSRF.");
        foreach (var email in new[] { "legacy@example.test", "new@example.test" })
        {
            var next = await Post("/account/check-email", start, ("Email", email), ("ReturnUrl", "/orders/new"), ("ReferralCode", "0123456789ABCDEF"));
            var html = await next.Content.ReadAsStringAsync();
            Check(next.StatusCode == HttpStatusCode.OK && html.Contains("id=\"transfer-id\""), "Missing email/password must show transfer.");
            Check(html.Contains("legacy-transfer-guide.png") && html.Contains("https://youmi-esports-mm5gly2p.manus.space/") && html.Contains("target=\"_blank\""), "Transfer guide/link missing.");
            Check(html.Contains("value=\"/orders/new\"") && html.Contains("value=\"0123456789ABCDEF\""), "Flow lost return path or referral.");
            var register = await Post("/account/register/start", html, ("Email", email), ("ReturnUrl", "/orders/new"), ("ReferralCode", "0123456789ABCDEF"));
            var registration = await register.Content.ReadAsStringAsync();
            Check(registration.Contains("id=\"register-password\"") && registration.Contains("value=\"" + email + "\""), "No-code registration lost selected email.");
        }
        var transfer = await Post("/account/transfer", start, ("Email", "transfer@example.test"), ("LegacyId", "77"), ("ReferralCode", "0123456789ABCDEF"));
        var transferHtml = await transfer.Content.ReadAsStringAsync();
        Check(transfer.StatusCode == HttpStatusCode.OK && transferHtml.Contains("data-transfer-name") && transferHtml.Contains("Legacy"), "Valid transfer code must display the account name for confirmation.");
        Check(!transferHtml.Contains("type=\"file\""), "Screenshot upload must not appear in the normal confirmation flow.");
        Check(WebUtility.HtmlDecode(transferHtml).Contains("金悠咪"), "Transfer confirmation must show VIP calculated from eligible consumption only.");
        Check(!transferHtml.Contains("legacy@example.test"), "Confirmation must not disclose the old Email.");
        Check(transferHtml.Contains("ref=0123456789ABCDEF"), "Transfer confirmation lost referral in the back link.");
        var flagged = await Post("/account/transfer", start, ("Email", "flagged-new@example.test"), ("LegacyId", "78"));
        var flaggedHtml = await flagged.Content.ReadAsStringAsync();
        Check(WebUtility.HtmlDecode(flaggedHtml).Contains("查無可轉移帳號") && !flaggedHtml.Contains("data-transfer-name"),
            "HasLoggedInNewSite=true must show not found even without a password or Google subject.");
        var pending = await Post("/account/transfer", start, ("Email", "pending-new@example.test"), ("LegacyId", "79"));
        var pendingHtml = await pending.Content.ReadAsStringAsync();
        Check(pendingHtml.Contains("data-transfer-name") && pendingHtml.Contains("Pending"),
            "HasLoggedInNewSite=false must show the name even with stored credentials.");
        Check(WebUtility.HtmlDecode(pendingHtml).Contains("一般會員"), "A member without consumption must show the general tier.");
        var tampered = await Post("/account/transfer/confirm", start, ("TransferToken", "invalid-token"));
        Check(tampered.StatusCode == HttpStatusCode.Redirect, "Tampered ticket must be rejected.");
        var skipConfirmation = await Post("/account/transfer/complete", start, ("TransferToken", TransferToken(transferHtml)), ("Password", "Password123!"), ("ConfirmPassword", "Password123!"));
        Check(skipConfirmation.StatusCode == HttpStatusCode.Redirect, "Unconfirmed ticket must not complete a transfer.");
        var notMine = await Post("/account/transfer/support/start", transferHtml, ("TransferToken", TransferToken(transferHtml)));
        var notMineHtml = await notMine.Content.ReadAsStringAsync();
        Check(notMine.StatusCode == HttpStatusCode.OK && notMineHtml.Contains("type=\"file\""), "Not-my-account must offer screenshot upload.");
        Check(notMineHtml.Contains("ref=0123456789ABCDEF"), "Support flow lost referral in the back link.");
        var missing = await Post("/account/transfer", start, ("Email", "missing@example.test"), ("LegacyId", "999999"));
        var missingHtml = await missing.Content.ReadAsStringAsync();
        Check(missing.StatusCode == HttpStatusCode.OK && WebUtility.HtmlDecode(missingHtml).Contains("查無可轉移帳號") && missingHtml.Contains("type=\"file\""),
            "A submitted ID absent from the database must show support automatically.");
        async Task<HttpResponseMessage> Upload(string html, byte[] bytes)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(Token(html)), "__RequestVerificationToken");
            form.Add(new StringContent(TransferToken(html)), "TransferToken");
            form.Add(new ByteArrayContent(bytes), "screenshot", "screenshot.png");
            return await client.PostAsync("/account/transfer/support", form);
        }
        var invalidImage = await Upload(missingHtml, System.Text.Encoding.UTF8.GetBytes("<svg onload='alert(1)'></svg>"));
        Check((await invalidImage.Content.ReadAsStringAsync()).Contains("validation-summary-errors"), "Invalid image accepted.");
        var oversized = await Upload(missingHtml, new byte[5 * 1024 * 1024 + 1]);
        Check((await oversized.Content.ReadAsStringAsync()).Contains("validation-summary-errors"), "Oversized image accepted.");
        using var image = new MagickImage(MagickColors.Red, 2, 2);
        var png = image.ToByteArray(MagickFormat.Png);
        var uploaded = await Upload(missingHtml, png);
        Check(uploaded.StatusCode == HttpStatusCode.OK && WebUtility.HtmlDecode(await uploaded.Content.ReadAsStringAsync()).Contains("截圖已上傳"), "Missing-account evidence was not saved.");
        await Upload(missingHtml, png);
        var wrongAccountUpload = await Upload(notMineHtml, png);
        Check(wrongAccountUpload.StatusCode == HttpStatusCode.OK && WebUtility.HtmlDecode(await wrongAccountUpload.Content.ReadAsStringAsync()).Contains("截圖已上傳"), "Not-my-account evidence was not saved.");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DBContext>();
            var cases = await db.FrontendTransferSupport.ToListAsync();
            Check(cases.Count == 2 && cases.Any(x => x.LegacyId == 999999 && x.DisplayedName == null) && cases.Any(x => x.LegacyId == 77 && x.DisplayedName == "Legacy"), "Support cases lost submitted values or duplicate uploads created duplicate cases.");
            Check(cases.All(x => System.Text.Encoding.ASCII.GetString(x.Screenshot, 8, 4) == "WEBP"), "Evidence must be decoded and re-encoded.");
            Check((await db.FrontendMember.FindAsync(77)).Email == "legacy@example.test", "Support or unconfirmed ticket changed member identity.");
        }
        var confirmed = await Post("/account/transfer/confirm", transferHtml, ("TransferToken", TransferToken(transferHtml)));
        var setupHtml = await confirmed.Content.ReadAsStringAsync();
        Check(confirmed.StatusCode == HttpStatusCode.OK && setupHtml.Contains("id=\"transfer-password\"") && !setupHtml.Contains("type=\"file\""), "Confirmation must lead to password setup without screenshot.");
        var mismatch = await Post("/account/transfer/complete", setupHtml, ("TransferToken", TransferToken(setupHtml)), ("Password", "Password123!"), ("ConfirmPassword", "Mismatch"));
        Check((await mismatch.Content.ReadAsStringAsync()).Contains("validation-summary-errors"), "Transfer password confirmation not validated.");
        var completed = await Post("/account/transfer/complete", setupHtml, ("TransferToken", TransferToken(setupHtml)), ("Password", "Password123!"), ("ConfirmPassword", "Password123!"), ("Email", "tampered@example.test"), ("LegacyId", "78"));
        Check(completed.StatusCode == HttpStatusCode.Redirect && handler.CookieContainer.GetCookies(client.BaseAddress)["YoMi_Frontend.Auth"] != null, "Transfer did not sign in.");
        var transferSession = await client.GetStringAsync("/account/password");
        var replay = await Post("/account/transfer/complete", transferSession, ("TransferToken", TransferToken(setupHtml)), ("Password", "Changed123!"), ("ConfirmPassword", "Changed123!"));
        Check((await replay.Content.ReadAsStringAsync()).Contains("validation-summary-errors"), "Transfer replay was accepted.");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DBContext>();
            var migrated = await db.FrontendMember.FindAsync(77);
            Check(migrated.Email == "transfer@example.test" && migrated.Name == "Legacy" && migrated.HasLoggedInNewSite &&
                FrontendMemberPassword.Verify(migrated, "Password123!") != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed,
                "Transfer lost identity or trusted posted Email/ID.");
            var spending = await db.FrontendConsumptionRecord.SingleAsync(x => x.UserId == 77 && x.CountsForVip && !x.IsReversed);
            spending.Amount = 30000m;
            spending.CreatedAt = DateTime.UtcNow.AddMonths(-2).AddDays(-2);
            await db.SaveChangesAsync();
        }
        foreach (var path in new[] { "/vip", "/my-records" })
        {
            var paused = WebUtility.HtmlDecode(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
            Check(!paused.Contains("已降") && !paused.Contains("自動降一階"), path + " must not show downgrades while they are paused.");
            // The paused mechanism must still render correctly once enabled.
            VipRules.MonthlyDowngradeEnabled = true;
            try
            {
                var response = await client.GetAsync(path);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && html.Contains("已降 2 階") && html.Contains("目前對應 鑽悠咪"),
                    path + " must render the downgraded status and earned-tier explanation.");
                Check(html.Contains("69,999") && html.Contains("VIP 累積消費升級進度") && !html.Contains("還需 NT$-"),
                    path + " must retain correct earned-tier progress after downgrade.");
            }
            finally { VipRules.MonthlyDowngradeEnabled = false; }
        }
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DBContext>();
            db.FrontendConsumptionRecord.Add(new FrontendConsumptionRecord { UserId = 77, Amount = 1m, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        VipRules.MonthlyDowngradeEnabled = true;
        try
        {
            var renewedVip = WebUtility.HtmlDecode(await client.GetStringAsync("/vip"));
            Check(!renewedVip.Contains("因未持續消費") && renewedVip.Contains("預計於"),
                "A new consumption must remove the expired-status message and render the renewed deadline.");
        }
        finally { VipRules.MonthlyDowngradeEnabled = false; }
        Console.WriteLine("PASS HTTP: VIP downgrade, earned progress and renewed membership render on real member pages");
        await Post("/account/logout", transferSession);
        start = await Page();
        var password = await Post("/account/check-email", start, ("Email", "PASSWORD@example.test"));
        var passwordHtml = WebUtility.HtmlDecode(await password.Content.ReadAsStringAsync());
        Check(passwordHtml.Contains("id=\"login-password\""), "Existing password account did not show password input.");
        Check(passwordHtml.IndexOf("歡迎回來</h1>") < passwordHtml.IndexOf("data-login-name") &&
            passwordHtml.IndexOf("data-login-name") < passwordHtml.IndexOf("class=\"member-auth-email\"") &&
            passwordHtml.Contains("Password member"), "Member name must appear between welcome title and Email.");
        var invalid = await Post("/account/register", start, ("Email", "test@example.test"), ("Name", "Test"), ("Password", "Password123!"), ("ConfirmPassword", "Mismatch"));
        Check(invalid.StatusCode == HttpStatusCode.OK && (await invalid.Content.ReadAsStringAsync()).Contains("validation-summary-errors"), "Password confirmation not validated.");
        var registered = await Post("/account/register", start, ("Email", "new@example.test"), ("Name", "New member"), ("Password", "Password123!"), ("ConfirmPassword", "Password123!"), ("ReturnUrl", "https://external.test/"));
        Check(registered.StatusCode == HttpStatusCode.Redirect && registered.Headers.Location.OriginalString == "/", "Registration must not redirect to an external URL.");
        Check(handler.CookieContainer.GetCookies(client.BaseAddress)["YoMi_Frontend.Auth"] != null, "Registration issued no session.");
        var memberPage = await client.GetAsync("/account/password");
        Check(memberPage.StatusCode == HttpStatusCode.OK, "Password cookie rejected by production authentication.");
        var memberHtml = await memberPage.Content.ReadAsStringAsync();
        var logout = await Post("/account/logout", memberHtml);
        Check(logout.StatusCode == HttpStatusCode.Redirect, "Logout failed.");
        start = await Page();
        var wrong = await Post("/account/login", start, ("Email", "new@example.test"), ("Password", "wrong-password"));
        Check(wrong.StatusCode == HttpStatusCode.OK && handler.CookieContainer.GetCookies(client.BaseAddress)["YoMi_Frontend.Auth"] == null, "Wrong password issued a session.");
        Check((await wrong.Content.ReadAsStringAsync()).Contains("data-login-name") &&
            (await wrong.Content.ReadAsStringAsync()).Contains("New member"), "Wrong password must preserve the account name.");
        var signedIn = await Post("/account/login", start, ("Email", "NEW@example.test"), ("Password", "Password123!"), ("ReturnUrl", "/my-records"));
        Check(signedIn.StatusCode == HttpStatusCode.Redirect && signedIn.Headers.Location.OriginalString == "/my-records", "Password login/local return failed.");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DBContext>();
            Check(await db.FrontendMember.CountAsync(x => x.Email == "new@example.test") == 1, "Flow created duplicate accounts.");
        }
        Check((await client.GetAsync("/images/auth/legacy-transfer-guide.png")).StatusCode == HttpStatusCode.OK, "Guide asset missing.");
        Check((await client.GetAsync("/account/link-google")).StatusCode == HttpStatusCode.Redirect, "Google link without configured provider must fail gracefully.");
        var authenticatedForm = await client.GetStringAsync("/account/password");
        for (var attempt = 0; attempt < 11; attempt++)
        {
            var limited = await Post("/account/login", authenticatedForm, ("Email", "password@example.test"), ("Password", "wrong-password"));
            Check(limited.StatusCode == (attempt < 10 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests), $"Password attempt {attempt + 1}: unexpected status {limited.StatusCode}.");
        }
        Console.WriteLine("PASS HTTP: Email branches, automatic transfer confirmation, support uploads, invalid images, staged/tampered/replayed tickets, CSRF, cookies and logout");
        await app.StopAsync();
    }
}

sealed class EmailMoneyCheckDbContext : DBContext
{
    public EmailMoneyCheckDbContext(DbContextOptions<DBContext> options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // SQLite cannot aggregate decimal; mirror the existing admin checks using exact integer cents.
        modelBuilder.Entity<FrontendConsumptionRecord>().Property(x => x.Amount)
            .HasConversion(amount => (long)(amount * 100m), cents => cents / 100m);
    }
}
