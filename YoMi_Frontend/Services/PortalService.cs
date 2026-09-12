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

        public async Task AddPriceItemAsync(string tab, string name, string price, string note)
        {
            var sortOrder = await _db.FrontendPriceItem.Where(x => x.Tab == tab).MaxAsync(x => (int?)x.SortOrder) ?? -1;
            var now = DateTime.UtcNow;
            _db.FrontendPriceItem.Add(new FrontendPriceItem
            {
                Tab = tab,
                Name = name.Trim(),
                Price = price.Trim(),
                Note = note?.Trim() ?? string.Empty,
                SortOrder = sortOrder + 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _db.SaveChangesAsync();
        }

        public async Task UpdatePriceItemAsync(int id, string tab, string name, string price, string note)
        {
            var item = await _db.FrontendPriceItem.FindAsync(id) ?? throw new InvalidOperationException("找不到價目項目");
            item.Tab = tab;
            item.Name = name.Trim();
            item.Price = price.Trim();
            item.Note = note?.Trim() ?? string.Empty;
            item.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public async Task MovePriceItemAsync(int id, int direction)
        {
            var item = await _db.FrontendPriceItem.FindAsync(id) ?? throw new InvalidOperationException("找不到價目項目");
            var items = await _db.FrontendPriceItem
                .Where(x => x.Tab == item.Tab)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .ToListAsync();
            var currentIndex = items.FindIndex(x => x.Id == id);
            var newIndex = currentIndex + Math.Sign(direction);
            if (currentIndex < 0 || newIndex < 0 || newIndex >= items.Count) return;

            (items[currentIndex], items[newIndex]) = (items[newIndex], items[currentIndex]);
            for (var index = 0; index < items.Count; index++)
            {
                items[index].SortOrder = index;
                items[index].UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
        }

        public async Task DeletePriceItemAsync(int id)
        {
            var item = await _db.FrontendPriceItem.FindAsync(id) ?? throw new InvalidOperationException("找不到價目項目");
            _db.Remove(item);
            await _db.SaveChangesAsync();
        }

        public Task<List<FrontendSiteSetting>> GetSettingItemsAsync()
        {
            return _db.FrontendSiteSetting.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        public async Task UpdateSettingAsync(string key, string value)
        {
            var setting = await _db.FrontendSiteSetting.FirstOrDefaultAsync(x => x.Key == key)
                ?? throw new InvalidOperationException("找不到網站設定");
            setting.Value = value?.Trim() ?? string.Empty;
            setting.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        public Task<List<FrontendBackgroundAsset>> GetBackgroundsAsync()
        {
            return _db.FrontendBackgroundAsset.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync();
        }

        public async Task AddBackgroundAsync(string name, string fileKey, string fileUrl, string mimeType)
        {
            _db.FrontendBackgroundAsset.Add(new FrontendBackgroundAsset
            {
                Name = name.Trim(),
                FileKey = fileKey,
                FileUrl = fileUrl,
                MimeType = mimeType,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        public async Task SetActiveBackgroundAsync(int? id)
        {
            var backgrounds = await _db.FrontendBackgroundAsset.Where(x => x.IsActive || x.Id == id).ToListAsync();
            if (id.HasValue && backgrounds.All(x => x.Id != id.Value)) throw new InvalidOperationException("找不到背景素材");

            foreach (var background in backgrounds)
            {
                background.IsActive = id.HasValue && background.Id == id.Value;
            }
            await _db.SaveChangesAsync();
        }

        public async Task<FrontendBackgroundAsset> DeleteBackgroundAsync(int id)
        {
            var background = await _db.FrontendBackgroundAsset.FindAsync(id) ?? throw new InvalidOperationException("找不到背景素材");
            _db.Remove(background);
            await _db.SaveChangesAsync();
            return background;
        }

        private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    }
}
