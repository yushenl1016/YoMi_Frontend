using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Authorize(Roles = "admin"), Route("admin")]
    public class AdminController : Controller
    {
        private static readonly string[] Tabs = { "users", "admins" };
        private readonly PortalService _portal;

        public AdminController(PortalService portal)
        {
            _portal = portal;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(string tab = "users", string priceTab = "基礎", string search = null, int? memberId = null)
        {
            if (tab == "price" || tab == "settings" || tab == "background") return NotFound();
            if (Array.IndexOf(Tabs, tab) < 0) tab = "users";


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

        private static string Limit(string value, int length)
        {
            value = value?.Trim() ?? string.Empty;
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private static string SafeMessage(Exception exception) =>
            exception is InvalidOperationException || exception is ArgumentException
                ? exception.Message
                : "操作失敗，請稍後再試";

    }
}
