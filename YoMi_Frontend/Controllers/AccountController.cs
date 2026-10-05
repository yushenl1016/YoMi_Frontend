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
        // Legacy dependencies retained for reference while password endpoints are disabled.
        // private readonly PortalService _portal;
        // private readonly IPasswordHasher<FrontendMember> _passwordHasher;
        // private readonly IConfiguration _configuration;
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
        public async Task<IActionResult> Index(string returnUrl = null, [FromQuery(Name = "ref")] string referralCode = null)
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
            ViewData["GoogleEnabled"] = await _schemes.GetSchemeAsync(Startup.GoogleScheme) != null;
            if (Request.Query.ContainsKey("googleError"))
                ModelState.AddModelError(string.Empty, "Google 登入未完成或身分驗證失敗，請重新嘗試。");
            return View(new AccountViewModel { ReturnUrl = returnUrl, ReferralCode = referralCode?.Trim().ToUpperInvariant() });
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
            await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
            if (!external.Succeeded || !external.Properties.Items.TryGetValue("Purpose", out var purpose) || purpose != "member")
                return RedirectToAction(nameof(Index));
            external.Properties.Items.TryGetValue("ReferralCode", out var referralCode);
            external.Properties.Items.TryGetValue("ReturnUrl", out var returnUrl);
            try
            {
                var member = await _googleLogin.SignInAsync(external.Principal, referralCode);
                await AuthCookie.SignInAsync(HttpContext, member);
                return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect("/");
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
            return RedirectToAction(nameof(Index), new { returnUrl, @ref = referralCode });
        }

        /* Legacy password login/registration intentionally disabled (including API endpoints).
           PasswordHash now stores Google sub, not a password hash. Do not re-enable unchanged.
        [AllowAnonymous, HttpPost("check-email"), ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > 320)
            {
                return BadRequest(new { message = "請輸入有效的郵箱" });
            }

            return Json(new { exists = await _portal.FindMemberByEmailAsync(email) != null });
        }

        [AllowAnonymous, HttpPost("login"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(AccountViewModel model)
        {
            if (!ModelState.IsValid)
            {
                ViewData["AuthStep"] = "password-login";
                return View("Index", model);
            }

            var member = await _portal.FindMemberByEmailAsync(model.Email);
            var result = member == null
                ? PasswordVerificationResult.Failed
                : _passwordHasher.VerifyHashedPassword(member, member.PasswordHash, model.Password);
            if (result == PasswordVerificationResult.Failed)
            {
                ModelState.AddModelError(string.Empty, "郵箱或密碼不正確");
                ViewData["AuthStep"] = "password-login";
                return View("Index", model);
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                await _portal.UpdatePasswordHashAsync(member, _passwordHasher.HashPassword(member, model.Password));
            }
            await _portal.UpdateLastSignInAsync(member);
            await AuthCookie.SignInAsync(HttpContext, member);

            return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl) : RedirectToAction("Index", "Home");
        }

        [AllowAnonymous, HttpPost("register"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (!ModelState.IsValid)
            {
                ViewData["RegisterModel"] = model;
                ViewData["AuthStep"] = "register";
                return View("Index", new AccountViewModel { Email = model.Email });
            }

            if (await _portal.FindMemberByEmailAsync(model.Email) != null)
            {
                ModelState.AddModelError(nameof(model.Email), "此郵箱已被註冊");
                ViewData["RegisterModel"] = model;
                ViewData["AuthStep"] = "register";
                return View("Index", new AccountViewModel { Email = model.Email });
            }

            var bootstrapEmail = _configuration["BootstrapAdminEmail"];
            var isAdmin = !string.IsNullOrWhiteSpace(bootstrapEmail) &&
                string.Equals(bootstrapEmail.Trim(), model.Email.Trim(), StringComparison.OrdinalIgnoreCase);
            var draft = new FrontendMember { Email = model.Email, Name = model.Name };

            try
            {
                var member = await _portal.CreateMemberAsync(
                    model.Email,
                    model.Name,
                    _passwordHasher.HashPassword(draft, model.Password),
                    isAdmin);
                await AuthCookie.SignInAsync(HttpContext, member);
                return RedirectToAction("Index", "Home");
            }
            catch (DbUpdateException)
            {
                ModelState.AddModelError(nameof(model.Email), "此郵箱已被註冊");
                ViewData["RegisterModel"] = model;
                ViewData["AuthStep"] = "register";
                return View("Index", new AccountViewModel { Email = model.Email });
            }
        }

        */

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
