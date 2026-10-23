using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;

static class EmailLoginChecks
{
    public static async Task Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Reject(Func<Task> action)
        {
            try { await action(); }
            catch (ArgumentException) { return; }
            catch (UnauthorizedAccessException) { return; }
            throw new Exception("Expected rejected credentials or duplicate registration.");
        }
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new DBContext(new DbContextOptionsBuilder<DBContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var login = new EmailMemberLogin(db);
        db.FrontendMember.Add(new FrontendMember { Id = 77, Email = "legacy@example.test", Name = "Legacy", PasswordHash = "old-open-id" });
        await db.SaveChangesAsync();
        Check(!await login.HasPasswordAsync("unknown@example.test"), "Unknown email must show transfer.");
        Check(!await login.HasPasswordAsync(" LEGACY@EXAMPLE.TEST "), "Existing email without password must show transfer.");
        await Reject(() => login.SignInAsync("legacy@example.test", "old-open-id"));
        await Reject(() => login.RegisterAsync("legacy@example.test", "New", "Password123!"));
        var member = await login.RegisterAsync(" USER@EXAMPLE.TEST ", " Member ", "Password123!");
        Check(member.Email == "user@example.test" && member.Name == "Member" && member.Role == "user", "Registration normalization/defaults failed.");
        Check(member.PasswordHash == "" && member.EmailPasswordHash != "Password123!", "Google subject and password hash must be separate.");
        Check(await login.HasPasswordAsync(member.Email), "Password account must show password login.");
        await Reject(() => login.RegisterAsync("USER@example.test", "Duplicate", "Password123!"));
        await Reject(() => login.SignInAsync(member.Email, "wrong-password"));
        Check((await login.SignInAsync(" USER@example.test ", "Password123!")).Id == member.Id, "Password login lost member identity.");
        var google = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "new-google-sub"), new Claim("email", member.Email), new Claim("email_verified", "true") }, "Google"));
        var googleLogin = new GoogleMemberLogin(db);
        await Reject(() => googleLogin.SignInAsync(google));
        await Reject(() => googleLogin.SignInAsync(google, password: "incorrect"));
        var linked = await googleLogin.SignInAsync(google, password: "Password123!");
        Check(linked.Id == member.Id && linked.PasswordHash == "new-google-sub", "Google must link the existing password account.");
        Check((await googleLogin.SignInAsync(google)).Id == member.Id && (await login.SignInAsync(member.Email, "Password123!")).Id == member.Id, "Both methods must sign into one account.");
        Check(await db.FrontendMember.CountAsync() == 2, "Authentication created a duplicate member.");

        var authentication = new CheckAuthentication();
        using var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await AuthCookie.SignInAsync(context, member, "password");
        Check(AuthCookie.Matches(authentication.SignedIn, member), "Password cookie rejected.");
        Check(authentication.SignedIn.FindFirst("google_sub") == null, "Password authentication must not claim Google authentication.");
        context.User = authentication.SignedIn;
        member.Name = "Updated";
        await AuthCookie.SignInAsync(context, member);
        Check(AuthCookie.Matches(authentication.SignedIn, member), "Profile update lost password authentication method.");
        member.EmailPasswordHash = FrontendMemberPassword.Hash(member, "Replacement123!");
        Check(!AuthCookie.Matches(authentication.SignedIn, member), "Password replacement must invalidate prior password sessions.");
        await AuthCookie.SignInAsync(context, member, "google");
        Check(AuthCookie.Matches(authentication.SignedIn, member), "Google cookie rejected after adding password support.");
        member.PasswordHash = "changed-sub";
        Check(!AuthCookie.Matches(authentication.SignedIn, member), "Google binding replacement must invalidate Google sessions.");
        db.ChangeTracker.Clear();
        var googleOnly = await googleLogin.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "google-only-sub"), new Claim("email", "google-only@example.test"), new Claim("email_verified", "true")
        }, "Google")));
        await Reject(() => login.SetInitialPasswordAsync(googleOnly.Id, null, "Password123!"));
        await Reject(() => login.SetInitialPasswordAsync(googleOnly.Id, "wrong-sub", "Password123!"));
        await login.SetInitialPasswordAsync(googleOnly.Id, "google-only-sub", "Password123!");
        Check((await login.SignInAsync(googleOnly.Email, "Password123!")).Id == googleOnly.Id, "Adding a password to Google changed member identity.");
        Check((await db.FrontendMember.FindAsync(googleOnly.Id)).PasswordHash == "google-only-sub", "Setting password changed Google binding.");
        await Reject(() => login.SetInitialPasswordAsync(googleOnly.Id, "google-only-sub", "Replacement123!"));
        Check((await login.SignInAsync(googleOnly.Email, "Password123!")).Id == googleOnly.Id, "Rejected password replacement changed the password.");
        foreach (var invalidReferral in new[] { "not-a-code", "EEEEEEEEEEEEEEEE" })
        {
            await Reject(() => login.RegisterAsync("bad-referral@example.test", "Test", "Password123!", invalidReferral));
            db.ChangeTracker.Clear();
            Check(!await db.FrontendMember.AnyAsync(x => x.Email == "bad-referral@example.test"), "Referral failure left a partial password registration.");
        }
        db.ChangeTracker.Clear();
        var legacy = await login.FindLegacyTransferAsync(77);
        db.FrontendMember.AddRange(
            new FrontendMember { Id = 1000, Email = "admin@example.test", Name = "Admin", Role = "admin" },
            new FrontendMember { Id = 1001, Email = "claimed@example.test", Name = "Claimed", HasLoggedInNewSite = true },
            new FrontendMember { Id = 1002, Email = "old-password@example.test", Name = "Old password", EmailPasswordHash = "imported-hash" },
            new FrontendMember { Id = 1003, Email = "old-google@example.test", Name = "Old Google", PasswordHash = "legacy-sub" },
            new FrontendMember { Id = 1004, Email = "new-google@example.test", Name = "New Google", PasswordHash = "new-site-sub", HasLoggedInNewSite = true });
        db.FrontendConsumptionRecord.Add(new FrontendConsumptionRecord { UserId = 77, Amount = 123m });
        await db.SaveChangesAsync();
        foreach (var unavailableId in new[] { 9999, 1001, 1004, member.Id })
            Check(await login.FindLegacyTransferAsync(unavailableId) == null,
                "Missing ID or HasLoggedInNewSite=true must return no transferable account.");
        foreach (var existingId in new[] { 77, 1000, 1002, 1003 })
            Check((await login.FindLegacyTransferAsync(existingId))?.Id == existingId,
                "HasLoggedInNewSite=false must resolve regardless of role or stored credentials.");
        var claimed = await db.FrontendMember.FindAsync(1001);
        await Reject(() => login.CompleteLegacyTransferAsync(claimed.Id, claimed.Email, claimed.UpdatedAt, "new-claimed@example.test", "Password123!"));
        foreach (var transferableId in new[] { 1000, 1002, 1003 })
        {
            var candidate = await login.FindLegacyTransferAsync(transferableId);
            var completed = await login.CompleteLegacyTransferAsync(candidate.Id, candidate.Email, candidate.UpdatedAt, $"transfer-{candidate.Id}@example.test", "Password123!");
            Check(completed.HasLoggedInNewSite && completed.Role == candidate.Role && completed.PasswordHash == "",
                "Completion must use the same flag-only eligibility as lookup and mark the account migrated.");
            Check(await login.FindLegacyTransferAsync(candidate.Id) == null, "Completed transfer must no longer be available.");
        }
        await Reject(() => login.CompleteLegacyTransferAsync(77, legacy.Email, legacy.UpdatedAt, " USER@example.test ", "Password123!"));
        await Reject(() => login.CompleteLegacyTransferAsync(77, "changed@example.test", legacy.UpdatedAt, "migrated@example.test", "Password123!"));
        await Reject(() => login.CompleteLegacyTransferAsync(77, legacy.Email, legacy.UpdatedAt.AddSeconds(1), "migrated@example.test", "Password123!"));
        db.ChangeTracker.Clear();
        Check((await db.FrontendMember.FindAsync(77)).Email == legacy.Email, "Rejected migration changed Email.");
        var migrated = await login.CompleteLegacyTransferAsync(77, legacy.Email, legacy.UpdatedAt, " MIGRATED@EXAMPLE.TEST ", "Password123!");
        Check(migrated.Id == 77 && migrated.Name == legacy.Name && migrated.CreatedAt == legacy.CreatedAt &&
            migrated.Role == legacy.Role && migrated.Email == "migrated@example.test" && migrated.PasswordHash == "" &&
            await db.FrontendConsumptionRecord.AnyAsync(x => x.UserId == 77 && x.Amount == 123m), "Public migration changed member/history.");
        await Reject(() => login.CompleteLegacyTransferAsync(77, legacy.Email, legacy.UpdatedAt, "again@example.test", "Changed123!"));
        Check(await login.FindLegacyTransferAsync(77) == null, "HasLoggedInNewSite must block another transfer after completion.");
        Console.WriteLine("PASS email authentication and transfer eligibility, duplicate Email, stale tickets, replay, retained ID/history");
    }
}
