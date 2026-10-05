using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;

namespace YoMi_Frontend.Services
{
    public sealed class GoogleMemberLogin
    {
        public const string CutoffConfigId = "FrontendGoogleMigrationCutoff";
        private readonly DBContext _db;

        public GoogleMemberLogin(DBContext db) => _db = db;

        // Only called with the principal produced by the Google OIDC token validator.
        public async Task<FrontendMember> SignInAsync(ClaimsPrincipal principal, string referralCode = null)
        {
            var sub = principal?.FindFirstValue("sub");
            var email = principal?.FindFirstValue("email")?.Trim().ToLowerInvariant();
            if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(sub) ||
                sub.Length > 255 || sub != sub.Trim() || string.IsNullOrWhiteSpace(email) ||
                email.Length > 320 || !new EmailAddressAttribute().IsValid(email) ||
                !string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Google 未提供有效且已驗證的 Email 與身分資料。");
            }

            // Atomic lookup/binding prevents simultaneous first logins from overwriting a binding.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var member = await _db.FrontendMember.SingleOrDefaultAsync(x => x.Email == email);
            var isNewMember = member == null;
            if (await _db.FrontendMember.AnyAsync(x => x.PasswordHash == sub && x.Email != email))
                throw new UnauthorizedAccessException("此 Google 帳號已對應其他會員 Email，請聯絡管理員。");

            var now = DateTime.UtcNow;
            if (member == null)
            {
                var name = principal.FindFirstValue("name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = email.Split('@')[0];
                member = new FrontendMember
                {
                    Email = email,
                    Name = name.Length > 64 ? name.Substring(0, 64) : name,
                    PasswordHash = sub,
                    Role = "user",
                    CreatedAt = now
                };
                _db.FrontendMember.Add(member);
            }
            else
            {
                var cutoffText = await _db.SysConfig.AsNoTracking()
                    .Where(x => x.Id == CutoffConfigId).Select(x => x.Value).SingleOrDefaultAsync();
                // SysConfig is edited in Taiwan time; member timestamps are stored in UTC.
                var validCutoff = DateTime.TryParseExact(cutoffText, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var taiwanCutoff);
                var canBindLegacy = validCutoff && !member.HasLoggedInNewSite &&
                    member.CreatedAt <= new DateTimeOffset(taiwanCutoff, TimeSpan.FromHours(8)).UtcDateTime;
                if (canBindLegacy)
                    member.PasswordHash = sub;
                else if (!string.Equals(member.PasswordHash, sub, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Google 帳號與既有會員的 Email／sub 不符，請聯絡管理員。");
            }

            member.HasLoggedInNewSite = true;
            member.LastSignedIn = now;
            member.UpdatedAt = now;
            await _db.SaveChangesAsync();
            // Registration and referral binding commit together; existing members are never rebound by a link.
            if (isNewMember && !string.IsNullOrWhiteSpace(referralCode))
                await new PortalService(_db).BindReferrerAsync(member.Id, referralCode);
            await transaction.CommitAsync();
            return member;
        }
    }
}
