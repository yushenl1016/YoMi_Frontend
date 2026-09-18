using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Models;
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
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
        }

        public async Task<decimal> GetTotalConsumptionAsync(int userId)
        {
            return await _db.FrontendConsumptionRecord
                .AsNoTracking()
                .Where(x => x.UserId == userId)
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
            record.Amount = amount;
            record.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            record.Category = string.IsNullOrWhiteSpace(category) ? "一般消費" : category.Trim();
            record.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task DeleteConsumptionAsync(int id)
        {
            var record = await _db.FrontendConsumptionRecord.FindAsync(id) ?? throw new InvalidOperationException("找不到消費記錄");
            _db.Remove(record);
            await _db.SaveChangesAsync();
        }


        private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    }
}
