using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Authorize, Route("my-records")]
    public class MemberController : Controller
    {
        private readonly PortalService _portal;
        private readonly ILogger<MemberController> _logger;

        public MemberController(PortalService portal, ILogger<MemberController> logger)
        {
            _portal = portal;
            _logger = logger;
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

        [HttpGet("referral-code"), ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> ReferralCode()
        {
            var id = AuthCookie.GetMemberId(User);
            if (id <= 0) return Unauthorized();
            var code = await _portal.GetReferralCodeAsync(id);
            return code == null ? NotFound() : Json(new { code });
        }

        [HttpPost("referrer/preview"), ValidateAntiForgeryToken, ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> PreviewReferrer(string code)
        {
            var id = AuthCookie.GetMemberId(User);
            if (id <= 0) return Unauthorized();
            try
            {
                var referrer = await _portal.PreviewReferrerAsync(id, code);
                return Json(new { name = referrer.Name, code = referrer.Code });
            }
            catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not preview referrer for member {MemberId}", id);
                return StatusCode(500, new { message = "暫時無法查詢推薦人，請稍後再試。" });
            }
        }

        [HttpPost("referrer/bind"), ValidateAntiForgeryToken, ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
        public async Task<IActionResult> BindReferrer(string code)
        {
            var id = AuthCookie.GetMemberId(User);
            if (id <= 0) return Unauthorized();
            try
            {
                await _portal.BindReferrerAsync(id, code);
                return Json(new { success = true });
            }
            catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not bind referrer for member {MemberId}", id);
                return StatusCode(500, new { message = "綁定未完成，請稍後重試。" });
            }
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

        [HttpPost("birthday"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Birthday(DateTime birthday)
        {
            try
            {
                if (!ModelState.IsValid) throw new ArgumentException("請輸入有效生日。");
                await _portal.SetBirthdayAsync(AuthCookie.GetMemberId(User), birthday);
                TempData["Success"] = "生日已設定，之後如需修改請聯絡客服。";
            }
            catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
            catch (Exception ex)
            { _logger.LogError(ex, "Member birthday save failed"); TempData["Error"] = "生日儲存失敗，請稍後再試。"; }
            return RedirectToAction(nameof(Index));
        }
    }
}
