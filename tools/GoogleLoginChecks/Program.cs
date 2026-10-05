using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Admin.Library.Models.System;
using YoMi_Admin.Library.Models.Manager;
using YoMi_Admin.Library.Models.Referral;
using YoMi_Frontend.Controllers;
using YoMi_Frontend.ViewModels;
using YoMi_Frontend.Services;

// Isolated in-memory database only. Never reads appsettings or connects to the shared SQL Server.
var cutoff = new DateTime(2026, 9, 18, 13, 44, 4, DateTimeKind.Utc);
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
ClaimsPrincipal Google(string email = "legacy@example.com", string sub = "google-sub", string verified = "true") =>
    new(new ClaimsIdentity(new[] { new Claim("sub", sub), new Claim("email", email), new Claim("email_verified", verified) }, "Google"));

async Task Run(string label, DateTime created, bool loggedIn, string storedSub, bool allow,
    string config = "2026-09-18 21:44:04", string incomingSub = "google-sub", string verified = "true")
{
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    await using var db = new DBContext(new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options);
    await db.Database.EnsureCreatedAsync();
    var oldTime = cutoff.AddDays(-1);
    db.FrontendMember.Add(new FrontendMember
    {
        Id = 77, Name = "Existing member", Email = "legacy@example.com", PasswordHash = storedSub,
        Role = "admin", CreatedAt = created, UpdatedAt = oldTime, LastSignedIn = oldTime,
        HasLoggedInNewSite = loggedIn
    });
    db.FrontendConsumptionRecord.Add(new FrontendConsumptionRecord
    {
        Id = 8, UserId = 77, Amount = 123.45m, CreatedBy = 77, CreatedAt = oldTime, UpdatedAt = oldTime
    });
    if (config != null) db.SysConfig.Add(new SysConfig { Id = GoogleMemberLogin.CutoffConfigId, Value = config });
    await db.SaveChangesAsync();
    db.ChangeTracker.Clear();
    var succeeded = false;
    try
    {
        var result = await new GoogleMemberLogin(db).SignInAsync(Google(" LEGACY@EXAMPLE.COM ", incomingSub, verified));
        Check(result.Id == 77, "Existing member ID changed.");
        succeeded = true;
    }
    catch (UnauthorizedAccessException) { }
    Check(succeeded == allow, label + ": wrong admission decision.");
    db.ChangeTracker.Clear();
    var after = await db.FrontendMember.SingleAsync();
    Check(after.CreatedAt == created && after.Name == "Existing member" && after.Role == "admin", "Legacy identity metadata changed.");
    Check(after.PasswordHash == (allow ? incomingSub : storedSub), "Wrong binding or rejected login changed sub.");
    Check(after.HasLoggedInNewSite == (allow || loggedIn), "Wrong migration flag.");
    Check(allow || (after.LastSignedIn == oldTime && after.UpdatedAt == oldTime), "Rejected login changed timestamps.");
    var record = await db.FrontendConsumptionRecord.SingleAsync();
    Check(record.UserId == 77 && record.Amount == 123.45m && record.CreatedBy == 77 && record.UpdatedAt == oldTime, "Consumption record changed.");
    Console.WriteLine("PASS " + label);
    passed++;
}

await Run("legacy first login binds by verified email", cutoff.AddSeconds(-1), false, "", true);
await Run("cutoff equality is included", cutoff, false, "old-open-id", true);
await Run("after cutoff rejects mismatched sub even on first login", cutoff.AddSeconds(1), false, "old-sub", false);
await Run("after cutoff accepts email and sub then marks migrated", cutoff.AddSeconds(1), false, "google-sub", true);
await Run("already migrated rejects mismatched sub before cutoff", cutoff.AddSeconds(-1), true, "old-sub", false);
await Run("already migrated accepts matching sub", cutoff.AddSeconds(-1), true, "google-sub", true);
await Run("both conditions fail: matching sub succeeds", cutoff.AddSeconds(1), true, "google-sub", true);
await Run("both conditions fail: wrong sub rejected", cutoff.AddSeconds(1), true, "old-sub", false);
await Run("missing cutoff does not grant email-only access", cutoff, false, "", false, config: null);
await Run("invalid cutoff does not grant email-only access", cutoff, false, "", false, config: "invalid");
await Run("missing cutoff still permits strict match", cutoff, true, "google-sub", true, config: null);
await Run("unverified Google email rejected", cutoff, false, "", false, verified: "false");
await Run("missing Google sub rejected", cutoff, false, "", false, incomingSub: "");
await Run("sub comparison is case-sensitive", cutoff, true, "Google-Sub", false);

await using (var connection = new SqliteConnection("Data Source=:memory:"))
{
    await connection.OpenAsync();
    await using var db = new DBContext(new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options);
    await db.Database.EnsureCreatedAsync();
    var service = new GoogleMemberLogin(db);
    var member = await service.SignInAsync(Google("new@example.com"));
    Check(member.Id > 0 && member.Role == "user" && member.HasLoggedInNewSite && member.PasswordHash == "google-sub", "New member defaults are incorrect.");
    var id = member.Id;
    db.ChangeTracker.Clear();
    member = await service.SignInAsync(Google("new@example.com"));
    Check(member.Id == id && await db.FrontendMember.CountAsync() == 1, "Repeated login created a duplicate.");
    try
    {
        await service.SignInAsync(Google("changed@example.com"));
        throw new Exception("Same sub with a different email must not create another member.");
    }
    catch (UnauthorizedAccessException) { }
    Check(await db.FrontendMember.CountAsync() == 1, "Rejected sub reuse created a member.");
    Console.WriteLine("PASS new registration, repeat login, duplicate sub protection");
    passed += 3;
}
await using (var connection = new SqliteConnection("Data Source=:memory:"))
{
    await connection.OpenAsync();
    await using var db = new DBContext(new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options);
    await db.Database.EnsureCreatedAsync();
    var owner = new Manager { Account = "referral-owner", Name = "推薦人" };
    var otherOwner = new Manager { Account = "other-referral-owner", Name = "另一位推薦人" };
    db.Manager.AddRange(owner, otherOwner);
    var referral = new ReferralAccount { Id = Guid.NewGuid(), ManagerId = owner.ManagerId, Code = "0123456789ABCDEF", CreatedAt = DateTime.UtcNow };
    var otherReferral = new ReferralAccount { Id = Guid.NewGuid(), ManagerId = otherOwner.ManagerId, Code = "FEDCBA9876543210", CreatedAt = DateTime.UtcNow };
    db.ReferralAccount.AddRange(referral, otherReferral); await db.SaveChangesAsync();
    var service = new GoogleMemberLogin(db);
    var member = await service.SignInAsync(Google("referred@example.test", "referred-sub"), referral.Code.ToLowerInvariant());
    Check((await db.ReferralBinding.SingleAsync(x => x.FrontendMemberId == member.Id)).ReferralAccountId == referral.Id,
        "New Google registration did not bind the URL referrer.");
    await service.SignInAsync(Google(member.Email, "referred-sub"), otherReferral.Code);
    Check(await db.ReferralBinding.CountAsync() == 1 && (await db.ReferralBinding.SingleAsync()).ReferralAccountId == referral.Id,
        "Existing account was rebound by a different registration link.");
    var unbound = await service.SignInAsync(Google("unbound@example.test", "unbound-sub"));
    await service.SignInAsync(Google(unbound.Email, "unbound-sub"), referral.Code);
    Check(!await db.ReferralBinding.AnyAsync(x => x.FrontendMemberId == unbound.Id), "A link bound an existing unbound account during login.");
    foreach (var invalidCode in new[] { "not-a-code", "EEEEEEEEEEEEEEEE" })
    {
        var count = await db.FrontendMember.CountAsync();
        try
        {
            await service.SignInAsync(Google("invalid@example.test", "invalid-sub"), invalidCode);
            throw new Exception("Invalid referral unexpectedly registered a member.");
        }
        catch (ArgumentException) { }
        db.ChangeTracker.Clear();
        Check(await db.FrontendMember.CountAsync() == count && !await db.FrontendMember.AnyAsync(x => x.Email == "invalid@example.test"),
            "Invalid referral left a partially registered account.");
    }
    Console.WriteLine("PASS referral signup binding, repeated login, existing unbound account, malformed and unknown code rollback");
    passed += 5;

    // Exercise the controller hand-off without contacting Google or issuing a real login cookie.
    var authentication = new CheckAuthentication();
    using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
    var schemes = new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions()));
    schemes.AddScheme(new AuthenticationScheme("Google", "Google", typeof(CookieAuthenticationHandler)));
    var context = new DefaultHttpContext { RequestServices = services };
    var controller = new AccountController(service, schemes, NullLogger<AccountController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = context, RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor() },
        TempData = new TempDataDictionary(context, new CheckTempData())
    };
    controller.Url = new UrlHelper(controller.ControllerContext);
    var registration = (AccountViewModel)((ViewResult)await controller.Index("/orders/new", referral.Code.ToLowerInvariant())).Model;
    Check(registration.ReferralCode == referral.Code, "Registration query code was not retained in the form model.");
    var challenge = (ChallengeResult)await controller.Google(registration.ReturnUrl, registration.ReferralCode);
    Check(challenge.Properties.Items["ReferralCode"] == referral.Code && challenge.Properties.Items["Purpose"] == "member",
        "Referral code was not carried in protected OAuth properties.");
    var externalRedirect = (ChallengeResult)await controller.Google("https://outside.example.test/", referral.Code);
    Check(externalRedirect.Properties.Items["ReturnUrl"] == "/", "Referral login introduced an open redirect.");
    context.Request.QueryString = new QueryString("?ref=" + otherReferral.Code);
    authentication.Result = AuthenticateResult.Success(new AuthenticationTicket(Google("callback@example.test", "callback-sub"),
        challenge.Properties, "GoogleExternal"));
    var result = await controller.GoogleComplete();
    var callbackMember = await db.FrontendMember.SingleAsync(x => x.Email == "callback@example.test");
    Check(result is LocalRedirectResult { Url: "/orders/new" } && authentication.SignedIn != null &&
        (await db.ReferralBinding.SingleAsync(x => x.FrontendMemberId == callbackMember.Id)).ReferralAccountId == referral.Id,
        "Google callback did not use the authenticated properties to register and bind the correct referrer.");
    Check(await controller.Google(null, "invalid") is RedirectToActionResult, "Malformed code reached the Google challenge.");
    schemes.RemoveScheme("Google");
    Check((await controller.Google(null, referral.Code) as RedirectToActionResult)?.RouteValues?["ref"]?.ToString() == referral.Code,
        "Unavailable Google login lost the referral on retry.");
    Console.WriteLine("PASS referral query/form/OAuth/callback hand-off, callback tamper resistance, local redirect and retry");
    passed += 6;
}
Console.WriteLine($"{passed} checks passed; no shared database was accessed.");

sealed class CheckAuthentication : IAuthenticationService
{
    public AuthenticateResult Result { get; set; } = AuthenticateResult.NoResult();
    public ClaimsPrincipal SignedIn { get; private set; }
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string scheme) => Task.FromResult(Result);
    public Task ChallengeAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string scheme, AuthenticationProperties properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string scheme, ClaimsPrincipal principal, AuthenticationProperties properties)
    { SignedIn = principal; return Task.CompletedTask; }
}
sealed class CheckTempData : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}
