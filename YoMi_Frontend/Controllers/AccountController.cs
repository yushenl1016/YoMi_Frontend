using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Route("account")]
    public class AccountController : Controller
    {
        private readonly GoogleMemberLogin _googleLogin;
        private readonly IAuthenticationSchemeProvider _schemes;
        private readonly ILogger<AccountController> _logger;

        public AccountController(GoogleMemberLogin googleLogin, IAuthenticationSchemeProvider schemes, ILogger<AccountController> logger)
        {
            _googleLogin = googleLogin;
            _schemes = schemes;
            _logger = logger;
        }

        [AllowAnonymous, HttpGet(""), HttpGet("/email-auth")]
        public async Task<IActionResult> Index(string returnUrl = null, [FromQuery(Name = "ref")] string referralCode = null, string email = null)
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
            ViewData["GoogleEnabled"] = await _schemes.GetSchemeAsync(Startup.GoogleScheme) != null;
            if (Request.Query.ContainsKey("googleError"))
                ModelState.AddModelError(string.Empty, "Google 登入未完成或身分驗證失敗，請重新嘗試。");
            return View(new AccountViewModel { ReturnUrl = returnUrl, ReferralCode = referralCode?.Trim().ToUpperInvariant(), Email = email?.Length <= 320 ? email : "" });
        }

        [AllowAnonymous, HttpPost("google"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Google(string returnUrl = null, string referralCode = null)
        {
            referralCode = referralCode?.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(referralCode) && (referralCode.Length != 16 || !referralCode.All(Uri.IsHexDigit)))
            {
                TempData["Error"] = "推薦連結無效，請重新開啟推薦人提供的連結。";
                return RedirectToAction(nameof(Index), new { returnUrl });
            }
            if (await _schemes.GetSchemeAsync(Startup.GoogleScheme) == null)
            {
                TempData["Error"] = "此環境尚未設定 Google 登入憑證，請聯絡管理員。";
                return RedirectToAction(nameof(Index), new { returnUrl, @ref = referralCode });
            }
            await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
            var properties = new AuthenticationProperties
            {
                RedirectUri = "/account/google-complete",
                IsPersistent = false
            };
            properties.Items["Purpose"] = "member";
            properties.Items["ReturnUrl"] = Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
            if (!string.IsNullOrEmpty(referralCode)) properties.Items["ReferralCode"] = referralCode;
            return Challenge(properties, Startup.GoogleScheme);
        }

        [AllowAnonymous, HttpGet("google-complete")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> GoogleComplete()
        {
            if (await _schemes.GetSchemeAsync(Startup.GoogleScheme) == null)
                return RedirectToAction(nameof(Index));
            var external = await HttpContext.AuthenticateAsync(Startup.GoogleExternalCookieScheme);
            if (!external.Succeeded || !external.Properties.Items.TryGetValue("Purpose", out var purpose) || purpose != "member")
            {
                await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
                return RedirectToAction(nameof(Index));
            }
            external.Properties.Items.TryGetValue("ReferralCode", out var referralCode);
            external.Properties.Items.TryGetValue("ReturnUrl", out var returnUrl);
            try
            {
                var member = await _googleLogin.SignInAsync(external.Principal, referralCode);
                await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
                await AuthCookie.SignInAsync(HttpContext, member, "google");
                return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect("/");
            }
            catch (GooglePasswordLinkRequiredException)
            {
                return LocalRedirect("/account/link-google");
            }
            catch (UnauthorizedAccessException error)
            {
                TempData["Error"] = error.Message;
            }
            catch (ArgumentException)
            {
                TempData["Error"] = "推薦連結無效，請重新開啟推薦人提供的連結。";
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Google member login could not be completed.");
                TempData["Error"] = "目前無法完成登入，請稍後重新嘗試或聯絡管理員。";
            }
            await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
            return RedirectToAction(nameof(Index), new { returnUrl, @ref = referralCode });
        }

        [Authorize, HttpPost("logout"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        [AllowAnonymous, HttpGet("access-denied")]
        public IActionResult AccessDenied()
        {
            Response.StatusCode = 403;
            return View();
        }
    }
}
