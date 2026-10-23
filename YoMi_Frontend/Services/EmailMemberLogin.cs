using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;

namespace YoMi_Frontend.Services
{
    public sealed class EmailMemberLogin
    {
        private readonly DBContext _db;
        public EmailMemberLogin(DBContext db) => _db = db;

        // shortcut: the requested ID-based claim has no ownership proof; require a secret or old-site assertion if this policy changes.
        public Task<FrontendMember> FindLegacyTransferAsync(int id) => _db.FrontendMember.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && !x.HasLoggedInNewSite);

        public async Task<FrontendMember> CompleteLegacyTransferAsync(int id, string originalEmail, DateTime originalUpdatedAt, string email, string password)
        {
            email = FrontendMemberPassword.NormalizeEmail(email);
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var member = await _db.FrontendMember.SingleOrDefaultAsync(x => x.Id == id);
            if (member == null || member.HasLoggedInNewSite ||
                member.Email != originalEmail || member.UpdatedAt != originalUpdatedAt)
                throw new ArgumentException("此帳號已轉移或資料已變更，請重新確認轉移碼。");
            if (await _db.FrontendMember.AnyAsync(x => x.Id != id && x.Email.ToLower() == email))
                throw new ArgumentException("此 Email 已被其他帳號使用，請返回改用其他 Email，或聯絡客服。");
            var hash = FrontendMemberPassword.Hash(member, password);
            member.Email = email;
            member.EmailPasswordHash = hash;
            member.PasswordHash = string.Empty;
            member.HasLoggedInNewSite = true;
            member.UpdatedAt = member.LastSignedIn = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return member;
        }

        public async Task<Guid> SaveTransferSupportAsync(Guid requestId, int legacyId, string email, string displayedName, byte[] screenshot)
        {
            email = FrontendMemberPassword.NormalizeEmail(email);
            if (requestId == Guid.Empty || legacyId <= 0) throw new ArgumentException("轉移資料無效，請重新輸入轉移碼。");
            if (await _db.FrontendTransferSupport.AnyAsync(x => x.Id == requestId)) return requestId;
            byte[] image;
            try { image = YoMi_Admin.Library.Models.Orders.PaymentProofImage.ToWebP(screenshot); }
            catch (ArgumentException ex) { throw new ArgumentException(ex.Message.Replace("付款證明", "舊站登入截圖")); }
            var request = new FrontendTransferSupport
            {
                Id = requestId, LegacyId = legacyId, Email = email, DisplayedName = displayedName,
                Screenshot = image, CreatedAt = DateTime.UtcNow
            };
            _db.FrontendTransferSupport.Add(request);
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                _db.Entry(request).State = EntityState.Detached;
                if (!await _db.FrontendTransferSupport.AnyAsync(x => x.Id == requestId)) throw;
            }
            return requestId;
        }

        public async Task<bool> HasPasswordAsync(string email) => await GetPasswordMemberNameAsync(email) != null;

        public Task<string> GetPasswordMemberNameAsync(string email)
        {
            email = FrontendMemberPassword.NormalizeEmail(email);
            return _db.FrontendMember.AsNoTracking()
                .Where(x => x.Email.ToLower() == email && x.EmailPasswordHash != null && x.EmailPasswordHash != "")
                .Select(x => x.Name).SingleOrDefaultAsync();
        }

        public async Task<FrontendMember> SignInAsync(string email, string password)
        {
            email = FrontendMemberPassword.NormalizeEmail(email);
            var member = await _db.FrontendMember.SingleOrDefaultAsync(x => x.Email.ToLower() == email);
            var result = FrontendMemberPassword.Verify(member, password);
            if (result == PasswordVerificationResult.Failed)
                throw new UnauthorizedAccessException("Email 或密碼不正確。");
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
                member.EmailPasswordHash = FrontendMemberPassword.Hash(member, password);
            member.HasLoggedInNewSite = true;
            member.LastSignedIn = member.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return member;
        }

        public async Task<FrontendMember> RegisterAsync(string email, string name, string password, string referralCode = null)
        {
            email = FrontendMemberPassword.NormalizeEmail(email);
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 64) throw new ArgumentException("名稱須為 1 至 64 個字元。");
            var member = new FrontendMember
            {
                Email = email, Name = name, Role = "user", HasLoggedInNewSite = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LastSignedIn = DateTime.UtcNow
            };
            member.EmailPasswordHash = FrontendMemberPassword.Hash(member, password);
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            if (await _db.FrontendMember.AnyAsync(x => x.Email.ToLower() == email))
                throw new ArgumentException("此 Email 已有帳號，請返回登入；尚未設定密碼的 Google 會員請先使用 Google 登入。");
            _db.FrontendMember.Add(member);
            await _db.SaveChangesAsync();
            if (!string.IsNullOrWhiteSpace(referralCode)) await new PortalService(_db).BindReferrerAsync(member.Id, referralCode);
            await transaction.CommitAsync();
            return member;
        }

        public async Task<FrontendMember> SetInitialPasswordAsync(int id, string googleSub, string password)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var member = await _db.FrontendMember.SingleOrDefaultAsync(x => x.Id == id);
            if (member == null || string.IsNullOrEmpty(googleSub) || member.PasswordHash != googleSub || !string.IsNullOrEmpty(member.EmailPasswordHash))
                throw new UnauthorizedAccessException("請使用 Google 登入尚未設定密碼的帳號後再試。");
            member.EmailPasswordHash = FrontendMemberPassword.Hash(member, password);
            member.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return member;
        }
    }
}
