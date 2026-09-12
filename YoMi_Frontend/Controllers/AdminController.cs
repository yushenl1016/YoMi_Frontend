using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Authorize(Roles = "admin"), Route("admin")]
    public class AdminController : Controller
    {
        private static readonly string[] Tabs = { "users", "price", "settings", "background", "admins" };
        private static readonly string[] PriceTabs = { "基礎", "趣味", "訂製" };
        private static readonly HashSet<string> UploadTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/png", "image/webp", "image/gif", "video/mp4", "video/webm"
        };
        private static readonly Dictionary<string, string> UploadExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",
            ["video/mp4"] = ".mp4",
            ["video/webm"] = ".webm"
        };

        private readonly PortalService _portal;
        private readonly IWebHostEnvironment _environment;

        public AdminController(PortalService portal, IWebHostEnvironment environment)
        {
            _portal = portal;
            _environment = environment;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(string tab = "users", string priceTab = "基礎", string search = null, int? memberId = null)
        {
            if (Array.IndexOf(Tabs, tab) < 0) tab = "users";
            if (Array.IndexOf(PriceTabs, priceTab) < 0) priceTab = "基礎";

            var model = new AdminPageViewModel
            {
                ActiveTab = tab,
                PriceTab = priceTab,
                Search = search?.Trim() ?? string.Empty,
                Stats = await _portal.GetAdminStatsAsync()
            };

            if (tab == "users" || tab == "admins")
            {
                var members = await _portal.GetMembersAsync(search);
                var selected = memberId.HasValue
                    ? members.FirstOrDefault(x => x.Member.Id == memberId.Value)?.Member ?? await _portal.FindMemberAsync(memberId.Value)
                    : null;
                model = new AdminPageViewModel
                {
                    ActiveTab = tab,
                    PriceTab = priceTab,
                    Search = model.Search,
                    Stats = model.Stats,
                    Members = members,
                    SelectedMember = selected,
                    SelectedRecords = selected == null
                        ? Array.Empty<YoMi_Admin.Library.Models.Frontend.FrontendConsumptionRecord>()
                        : await _portal.GetConsumptionRecordsAsync(selected.Id)
                };
            }
            else if (tab == "price")
            {
                model = Copy(model, priceItems: await _portal.GetPriceItemsAsync(priceTab));
            }
            else if (tab == "settings")
            {
                model = Copy(model, settings: await _portal.GetSettingItemsAsync());
            }
            else if (tab == "background")
            {
                model = Copy(model, backgrounds: await _portal.GetBackgroundsAsync());
            }

            return View(model);
        }

        [HttpPost("consumption/add"), ValidateAntiForgeryToken]
        public Task<IActionResult> AddConsumption(int userId, decimal amount, string description, string category) =>
            ExecuteAsync(
                () => _portal.AddConsumptionAsync(userId, amount, Limit(description, 1000), Limit(category, 64), AuthCookie.GetMemberId(User)),
                "users", "消費記錄已新增", userId);

        [HttpPost("consumption/update"), ValidateAntiForgeryToken]
        public Task<IActionResult> UpdateConsumption(int id, int userId, decimal amount, string description, string category) =>
            ExecuteAsync(
                () => _portal.UpdateConsumptionAsync(id, amount, Limit(description, 1000), Limit(category, 64)),
                "users", "消費記錄已更新", userId);

        [HttpPost("consumption/delete"), ValidateAntiForgeryToken]
        public Task<IActionResult> DeleteConsumption(int id, int userId) =>
            ExecuteAsync(() => _portal.DeleteConsumptionAsync(id), "users", "消費記錄已刪除", userId);

        [HttpPost("price/add"), ValidateAntiForgeryToken]
        public Task<IActionResult> AddPrice(string priceTab, string name, string price, string note)
        {
            if (!ValidPriceInput(priceTab, name, price)) return Invalid("請完整填寫價目資料", "price", priceTab: priceTab);
            return ExecuteAsync(() => _portal.AddPriceItemAsync(priceTab, Limit(name, 128), Limit(price, 64), Limit(note, 512)), "price", "價目項目已新增", priceTab: priceTab);
        }

        [HttpPost("price/update"), ValidateAntiForgeryToken]
        public Task<IActionResult> UpdatePrice(int id, string priceTab, string name, string price, string note)
        {
            if (!ValidPriceInput(priceTab, name, price)) return Invalid("請完整填寫價目資料", "price", priceTab: priceTab);
            return ExecuteAsync(() => _portal.UpdatePriceItemAsync(id, priceTab, Limit(name, 128), Limit(price, 64), Limit(note, 512)), "price", "價目項目已更新", priceTab: priceTab);
        }

        [HttpPost("price/move"), ValidateAntiForgeryToken]
        public Task<IActionResult> MovePrice(int id, int direction, string priceTab) =>
            ExecuteAsync(() => _portal.MovePriceItemAsync(id, direction), "price", "排序已更新", priceTab: priceTab);

        [HttpPost("price/delete"), ValidateAntiForgeryToken]
        public Task<IActionResult> DeletePrice(int id, string priceTab) =>
            ExecuteAsync(() => _portal.DeletePriceItemAsync(id), "price", "價目項目已刪除", priceTab: priceTab);

        [HttpPost("setting/update"), ValidateAntiForgeryToken]
        public Task<IActionResult> UpdateSetting(string key, string value) =>
            ExecuteAsync(() => _portal.UpdateSettingAsync(key, value), "settings", "網站設定已更新");

        [HttpPost("member/name"), ValidateAntiForgeryToken]
        public Task<IActionResult> UpdateMemberName(int userId, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64) return Invalid("用戶名稱必須為 1 至 64 個字", "admins");
            return ExecuteAsync(() => _portal.UpdateMemberNameAsync(userId, name), "admins", "會員名稱已更新");
        }

        [HttpPost("member/role"), ValidateAntiForgeryToken]
        public Task<IActionResult> UpdateMemberRole(int userId, string role)
        {
            if (userId == AuthCookie.GetMemberId(User)) return Invalid("不可修改自己的管理員角色", "admins");
            return ExecuteAsync(() => _portal.UpdateMemberRoleAsync(userId, role), "admins", "會員角色已更新");
        }

        [HttpPost("background/url"), ValidateAntiForgeryToken]
        public Task<IActionResult> AddBackgroundUrl(string name, string url)
        {
            var parsed = VideoUrlParser.Parse(url);
            if (!parsed.IsValid) return Invalid(parsed.Error, "background");
            var assetName = string.IsNullOrWhiteSpace(name) ? parsed.DisplayName : Limit(name, 128);
            return ExecuteAsync(
                () => _portal.AddBackgroundAsync(assetName, $"url:{parsed.Platform}:{parsed.DisplayName}", parsed.EmbedUrl, $"embed/{parsed.Platform}"),
                "background", "影片連結已新增");
        }

        [HttpPost("background/upload"), ValidateAntiForgeryToken, RequestFormLimits(MultipartBodyLengthLimit = 52428800)]
        public async Task<IActionResult> UploadBackground(string name, IFormFile file)
        {
            if (file == null || file.Length == 0) return await Invalid("請選擇背景檔案", "background");
            if (file.Length > 50 * 1024 * 1024) return await Invalid("背景檔案不可超過 50 MB", "background");
            if (!UploadTypes.Contains(file.ContentType)) return await Invalid("僅支援 JPG、PNG、WEBP、GIF、MP4 與 WEBM", "background");

            var extension = UploadExtensions[file.ContentType];
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var relativePath = $"uploads/backgrounds/{fileName}";
            var targetPath = Path.Combine(_environment.WebRootPath, "uploads", "backgrounds", fileName);
            await using (var stream = new FileStream(targetPath, FileMode.CreateNew))
            {
                await file.CopyToAsync(stream);
            }

            var assetName = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(file.FileName)
                : name.Trim();
            assetName = Limit(assetName, 128);
            try
            {
                await _portal.AddBackgroundAsync(assetName, relativePath, $"/{relativePath}", file.ContentType);
                TempData["Success"] = "背景已上傳";
            }
            catch
            {
                System.IO.File.Delete(targetPath);
                TempData["Error"] = "檔案已上傳，但背景資料無法儲存，請稍後再試。";
            }
            return RedirectToAction(nameof(Index), new { tab = "background" });
        }

        [HttpPost("background/activate"), ValidateAntiForgeryToken]
        public Task<IActionResult> ActivateBackground(int id) =>
            ExecuteAsync(() => _portal.SetActiveBackgroundAsync(id), "background", "背景已更新");

        [HttpPost("background/clear"), ValidateAntiForgeryToken]
        public Task<IActionResult> ClearBackground() =>
            ExecuteAsync(() => _portal.SetActiveBackgroundAsync(null), "background", "已清除背景");

        [HttpPost("background/delete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBackground(int id)
        {
            try
            {
                var background = await _portal.DeleteBackgroundAsync(id);
                DeleteLocalBackground(background.FileKey);
                TempData["Success"] = "背景已刪除";
            }
            catch (Exception ex)
            {
                TempData["Error"] = SafeMessage(ex);
            }
            return RedirectToAction(nameof(Index), new { tab = "background" });
        }

        private async Task<IActionResult> ExecuteAsync(Func<Task> action, string tab, string success, int? memberId = null, string priceTab = null)
        {
            try
            {
                await action();
                TempData["Success"] = success;
            }
            catch (Exception ex)
            {
                TempData["Error"] = SafeMessage(ex);
            }
            return RedirectToAction(nameof(Index), new { tab, memberId, priceTab });
        }

        private Task<IActionResult> Invalid(string message, string tab, int? memberId = null, string priceTab = null)
        {
            TempData["Error"] = message;
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Index), new { tab, memberId, priceTab }));
        }

        private static bool ValidPriceInput(string tab, string name, string price) =>
            Array.IndexOf(PriceTabs, tab) >= 0 && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(price);

        private static string Limit(string value, int length)
        {
            value = value?.Trim() ?? string.Empty;
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private static string SafeMessage(Exception exception) =>
            exception is InvalidOperationException || exception is ArgumentException
                ? exception.Message
                : "操作失敗，請稍後再試";

        private void DeleteLocalBackground(string fileKey)
        {
            if (string.IsNullOrWhiteSpace(fileKey) || !fileKey.StartsWith("uploads/backgrounds/", StringComparison.Ordinal)) return;
            var root = Path.GetFullPath(Path.Combine(_environment.WebRootPath, "uploads", "backgrounds")) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Path.Combine(_environment.WebRootPath, fileKey.Replace('/', Path.DirectorySeparatorChar)));
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(target))
            {
                System.IO.File.Delete(target);
            }
        }

        private static AdminPageViewModel Copy(
            AdminPageViewModel source,
            IReadOnlyList<YoMi_Admin.Library.Models.Frontend.FrontendPriceItem> priceItems = null,
            IReadOnlyList<YoMi_Admin.Library.Models.Frontend.FrontendSiteSetting> settings = null,
            IReadOnlyList<YoMi_Admin.Library.Models.Frontend.FrontendBackgroundAsset> backgrounds = null) =>
            new()
            {
                ActiveTab = source.ActiveTab,
                PriceTab = source.PriceTab,
                Search = source.Search,
                Stats = source.Stats,
                PriceItems = priceItems ?? source.PriceItems,
                Settings = settings ?? source.Settings,
                Backgrounds = backgrounds ?? source.Backgrounds
            };
    }
}
