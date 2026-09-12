using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Route("account")]
    public class AccountController : Controller
    {
        private readonly PortalService _portal;
        private readonly IPasswordHasher<FrontendMember> _passwordHasher;
        private readonly IConfiguration _configuration;

        public AccountController(PortalService portal, IPasswordHasher<FrontendMember> passwordHasher, IConfiguration configuration)
        {
            _portal = portal;
            _passwordHasher = passwordHasher;
            _configuration = configuration;
        }

        [AllowAnonymous, HttpGet(""), HttpGet("/email-auth")]
        public IActionResult Index(string returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
            return View(new AccountViewModel { ReturnUrl = returnUrl });
        }

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
