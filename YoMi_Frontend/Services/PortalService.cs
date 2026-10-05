using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Admin.Library.Models.Referral;
using YoMi_Admin.Library.Models.Orders;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Services
{
    public class PortalService
    {
        private readonly DBContext _db;

        public PortalService(DBContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync()
        {
            return await _db.FrontendSiteSetting
                .AsNoTracking()
                .ToDictionaryAsync(x => x.Key, x => x.Value);
        }

        public async Task<SiteChromeViewModel> GetSiteChromeAsync()
        {
            return new SiteChromeViewModel
            {
                Settings = await GetSettingsAsync(),
                Background = await _db.FrontendBackgroundAsset
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.IsActive)
            };
        }

        public Task<List<FrontendPriceItem>> GetPriceItemsAsync(string tab = null)
        {
            var query = _db.FrontendPriceItem.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(tab))
            {
                query = query.Where(x => x.Tab == tab);
            }

            return query.OrderBy(x => x.Tab).ThenBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        }

        public Task<FrontendMember> FindMemberByEmailAsync(string email)
        {
            var normalized = NormalizeEmail(email);
            return _db.FrontendMember.FirstOrDefaultAsync(x => x.Email == normalized);
        }

        public Task<FrontendMember> FindMemberAsync(int id)
        {
            return _db.FrontendMember.FirstOrDefaultAsync(x => x.Id == id);
        }

        public Task SetBirthdayAsync(int id, DateTime birthday) => new CustomerOrderService(_db).SetBirthdayAsync(id, birthday, OrderRules.TaiwanNow);

        public Task<string> GetReferralCodeAsync(int memberId) => _db.ReferralAccount.AsNoTracking()
            .Where(x => x.FrontendMemberId == memberId).Select(x => x.Code).SingleOrDefaultAsync();

        public async Task<MemberReferrerViewModel> GetReferrerAsync(int memberId)
        {
            var account = await (from binding in _db.ReferralBinding.AsNoTracking()
                                 join referrer in _db.ReferralAccount.AsNoTracking() on binding.ReferralAccountId equals referrer.Id
                                 where binding.FrontendMemberId == memberId
                                 select referrer).SingleOrDefaultAsync();
            return account == null ? null : await DescribeReferrerAsync(account);
        }

        public async Task<MemberReferrerViewModel> PreviewReferrerAsync(int memberId, string code) =>
            await DescribeReferrerAsync(await FindReferrerToBindAsync(memberId, code));

        public async Task BindReferrerAsync(int memberId, string code)
        {
            var account = await FindReferrerToBindAsync(memberId, code);
            var existing = await _db.ReferralBinding.AsNoTracking().SingleOrDefaultAsync(x => x.FrontendMemberId == memberId);
            if (existing != null)
            {
                if (existing.ReferralAccountId != account.Id)
                    throw new ArgumentException("你已綁定其他推薦人，不能重複或改綁。");
                return;
            }
            var binding = new ReferralBinding
            {
                FrontendMemberId = memberId,
                ReferralAccountId = account.Id,
                CreatedAt = DateTime.Now, // Referral timestamps in the backend use local time.
                CreatedBy = Guid.Empty // Self-service: member is FrontendMemberId, no acting manager.
            };
            _db.ReferralBinding.Add(binding);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                _db.Entry(binding).State = EntityState.Detached;
                // The member primary key arbitrates simultaneous frontend/backend submissions.
                var saved = await _db.ReferralBinding.AsNoTracking().SingleOrDefaultAsync(x => x.FrontendMemberId == memberId);
                if (saved == null) throw;
                if (saved.ReferralAccountId != account.Id)
                    throw new ArgumentException("你已綁定其他推薦人，不能重複或改綁。");
            }
        }

        private async Task<ReferralAccount> FindReferrerToBindAsync(int memberId, string code)
        {
            code = code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code) || code.Length != 16 || !code.All(Uri.IsHexDigit))
                throw new ArgumentException("請輸入有效的 16 碼推薦碼。");
            if (!await _db.FrontendMember.AnyAsync(x => x.Id == memberId))
                throw new ArgumentException("找不到會員，請重新登入。");
            var account = await _db.ReferralAccount.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code);
            if (account == null) throw new ArgumentException("推薦碼不存在，請確認後再試。");
            if (account.FrontendMemberId == memberId) throw new ArgumentException("不能使用自己的推薦碼。");
            var existing = await _db.ReferralBinding.AsNoTracking().SingleOrDefaultAsync(x => x.FrontendMemberId == memberId);
            if (existing != null && existing.ReferralAccountId != account.Id)
                throw new ArgumentException("你已綁定其他推薦人，不能重複或改綁。");
            return account;
        }

        private async Task<MemberReferrerViewModel> DescribeReferrerAsync(ReferralAccount account)
        {
            var name = account.ManagerId.HasValue
                ? await _db.Manager.Where(x => x.ManagerId == account.ManagerId).Select(x => x.Name).SingleOrDefaultAsync()
                : await _db.FrontendMember.Where(x => x.Id == account.FrontendMemberId).Select(x => x.Name).SingleOrDefaultAsync();
            return new MemberReferrerViewModel { Name = name, Code = account.Code };
        }

        public async Task<FrontendMember> CreateMemberAsync(string email, string name, string passwordHash, bool isAdmin)
        {
            var now = DateTime.UtcNow;
            var member = new FrontendMember
            {
                Email = NormalizeEmail(email),
                Name = name.Trim(),
                PasswordHash = passwordHash,
                Role = isAdmin ? "admin" : "user",
                CreatedAt = now,
                UpdatedAt = now,
                LastSignedIn = now
            };

            _db.FrontendMember.Add(member);
            await _db.SaveChangesAsync();
            return member;
        }

        public async Task UpdateLastSignInAsync(FrontendMember member)
        {
            member.LastSignedIn = DateTime.UtcNow;
            member.UpdatedAt = member.LastSignedIn;
            await _db.SaveChangesAsync();
        }

        public async Task UpdatePasswordHashAsync(FrontendMember member, string passwordHash)
        {
            member.PasswordHash = passwordHash;
            member.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task UpdateMemberNameAsync(int id, string name)
        {
            var member = await FindMemberAsync(id) ?? throw new InvalidOperationException("找不到會員");
            member.Name = name.Trim();
            member.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task UpdateMemberRoleAsync(int id, string role)
        {
            if (role != "user" && role != "admin")
            {
                throw new ArgumentException("無效的角色");
            }

            var member = await FindMemberAsync(id) ?? throw new InvalidOperationException("找不到會員");
            if (member.Role == "admin" && role == "user" && await _db.FrontendMember.CountAsync(x => x.Role == "admin") <= 1)
            {
                throw new InvalidOperationException("至少需要保留一位管理員");
            }

            member.Role = role;
            member.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task<List<AdminMemberViewModel>> GetMembersAsync(string search = null)
        {
            var query = _db.FrontendMember.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(x => x.Name.Contains(term) || x.Email.Contains(term));
            }

            var members = await query.OrderByDescending(x => x.CreatedAt).ToListAsync();
            var totals = await _db.FrontendConsumptionRecord
                .AsNoTracking()
                .Where(x => x.CountsForVip && !x.IsReversed)
                .GroupBy(x => x.UserId)
                .Select(x => new { UserId = x.Key, Total = x.Sum(y => y.Amount) })
                .ToDictionaryAsync(x => x.UserId, x => x.Total);

            return members.Select(member =>
            {
                var total = totals.TryGetValue(member.Id, out var value) ? value : 0m;
                return new AdminMemberViewModel
                {
                    Member = member,
                    Total = total,
                    Status = VipRules.GetStatus(total)
                };
            }).ToList();
        }

        public async Task<AdminStatsViewModel> GetAdminStatsAsync()
        {
            var totalUsers = await _db.FrontendMember.CountAsync();
            var totals = await _db.FrontendConsumptionRecord
                .AsNoTracking()
                .Where(x => !x.IsReversed)
                .GroupBy(x => x.UserId)
                .Select(x => x.Sum(y => y.Amount))
                .ToListAsync();
            var totalSpending = totals.Sum();

            return new AdminStatsViewModel
            {
                TotalUsers = totalUsers,
                VipMembers = totals.Count(x => x >= 3000m),
                TotalSpending = totalSpending,
                AverageSpending = totalUsers == 0 ? 0 : Math.Round(totalSpending / totalUsers)
            };
        }

        public Task<List<FrontendConsumptionRecord>> GetConsumptionRecordsAsync(int userId)
        {
            return _db.FrontendConsumptionRecord
                .AsNoTracking()
                .Where(x => x.UserId == userId && !x.IsReversed)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
        }

        public async Task<decimal> GetTotalConsumptionAsync(int userId)
        {
            return await _db.FrontendConsumptionRecord
                .AsNoTracking()
                .Where(x => x.UserId == userId && x.CountsForVip && !x.IsReversed)
                .SumAsync(x => (decimal?)x.Amount) ?? 0m;
        }

        public async Task AddConsumptionAsync(int userId, decimal amount, string description, string category, int createdBy)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "消費金額必須大於 0");
            if (!await _db.FrontendMember.AnyAsync(x => x.Id == userId)) throw new InvalidOperationException("找不到會員");

            var now = DateTime.UtcNow;
            _db.FrontendConsumptionRecord.Add(new FrontendConsumptionRecord
            {
                UserId = userId,
                Amount = amount,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "一般消費" : category.Trim(),
                CreatedBy = createdBy,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _db.SaveChangesAsync();
        }

        public async Task UpdateConsumptionAsync(int id, decimal amount, string description, string category)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "消費金額必須大於 0");
            var record = await _db.FrontendConsumptionRecord.FindAsync(id) ?? throw new InvalidOperationException("找不到消費記錄");
            if (record.CustomerOrderId.HasValue) throw new InvalidOperationException("訂單消費由報單審核管理，不能直接修改。");
            record.Amount = amount;
            record.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            record.Category = string.IsNullOrWhiteSpace(category) ? "一般消費" : category.Trim();
            record.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task DeleteConsumptionAsync(int id)
        {
            var record = await _db.FrontendConsumptionRecord.FindAsync(id) ?? throw new InvalidOperationException("找不到消費記錄");
            if (record.CustomerOrderId.HasValue) throw new InvalidOperationException("訂單消費由報單審核管理，不能直接刪除。");
            _db.Remove(record);
            await _db.SaveChangesAsync();
        }


        private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    }
}
