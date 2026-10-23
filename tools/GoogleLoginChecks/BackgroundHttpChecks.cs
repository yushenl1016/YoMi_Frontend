using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;

static class BackgroundHttpChecks
{
    public static async Task RunAsync(WebApplication app, HttpClient client)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DBContext>();
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        Check(!(await client.GetStringAsync("/")).Contains("has-site-background"), "No background must preserve original page theme.");
        var asset = new FrontendBackgroundAsset { Name = "Check", FileKey = "check.png", FileUrl = "/images/auth/legacy-transfer-guide.png", MimeType = "image/png", IsActive = true };
        var setting = new FrontendSiteSetting { Key = BackgroundAppearance.Key, Value = "65", Label = "遮罩" };
        db.FrontendBackgroundAsset.Add(asset);
        db.FrontendSiteSetting.Add(setting);
        await db.SaveChangesAsync();
        foreach (var route in new[] { "/", "/price", "/email-auth" })
        {
            var html = await client.GetStringAsync(route);
            Check(html.Contains("has-site-background") && html.Contains("--background-overlay-opacity:0.65") && html.Contains("class=\"background-shade\""), "Saved shade missing from " + route);
        }
        foreach (var value in new[] { "0", "100", "invalid" })
        {
            setting.Value = value;
            await db.SaveChangesAsync();
            var expected = value == "0" ? "0" : value == "100" ? "1" : "0.72";
            Check((await client.GetStringAsync("/")).Contains("--background-overlay-opacity:" + expected + "\""), "Overlay bound or default failed: " + value);
        }
        asset.MimeType = "video/mp4";
        asset.FileUrl = "/uploads/backgrounds/check.mp4";
        await db.SaveChangesAsync();
        var videoPage = await client.GetStringAsync("/email-auth");
        Check(videoPage.Contains("autoplay muted loop playsinline data-background-video") && videoPage.Contains("data-sound-toggle"), "MP4 must start muted and expose explicit sound control.");
        db.FrontendBackgroundAsset.Remove(asset);
        db.FrontendSiteSetting.Remove(setting);
        await db.SaveChangesAsync();
        Console.WriteLine("PASS HTTP: background image/video markup, saved global overlay, endpoints, invalid-value fallback and no-background theme.");
    }
}
