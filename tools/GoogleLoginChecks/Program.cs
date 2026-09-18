using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Admin.Library.Models.System;
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
Console.WriteLine($"{passed} checks passed; no shared database was accessed.");
