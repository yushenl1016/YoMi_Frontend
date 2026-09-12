using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using YoMi_Frontend.Models;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Authorize, Route("my-records")]
    public class MemberController : Controller
    {
        private readonly PortalService _portal;

        public MemberController(PortalService portal)
        {
            _portal = portal;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var id = AuthCookie.GetMemberId(User);
            var member = await _portal.FindMemberAsync(id);
            if (member == null)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return RedirectToAction("Index", "Account");
            }

            var total = await _portal.GetTotalConsumptionAsync(id);
            return View(new RecordsPageViewModel
            {
                Member = member,
                Records = await _portal.GetConsumptionRecordsAsync(id),
                Status = VipRules.GetStatus(total)
            });
        }

        [HttpPost("name"), ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateName(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            {
                TempData["Error"] = "用戶名稱必須為 1 至 64 個字";
                return RedirectToAction(nameof(Index));
            }

            var id = AuthCookie.GetMemberId(User);
            await _portal.UpdateMemberNameAsync(id, name);
            var member = await _portal.FindMemberAsync(id);
            await AuthCookie.SignInAsync(HttpContext, member);
            TempData["Success"] = "名稱已更新";
            return RedirectToAction(nameof(Index));
        }
    }
}
