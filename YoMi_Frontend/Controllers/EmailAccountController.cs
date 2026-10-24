using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;
using YoMi_Frontend.ViewModels;

namespace YoMi_Frontend.Controllers
{
    [Route("account"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class EmailAccountController : Controller
    {
        private readonly EmailMemberLogin _login;
        private readonly GoogleMemberLogin _google;
        private readonly AccountAttemptLimiter _attempts;
        private readonly ILogger<EmailAccountController> _logger;
        private readonly IAuthenticationSchemeProvider _schemes;
        private readonly ITimeLimitedDataProtector _transferProtector;
        private readonly PortalService _portal;
        public EmailAccountController(EmailMemberLogin login, GoogleMemberLogin google, AccountAttemptLimiter attempts, ILogger<EmailAccountController> logger, IAuthenticationSchemeProvider schemes, IDataProtectionProvider protection, PortalService portal)
        {
            _login = login; _google = google; _attempts = attempts; _logger = logger; _schemes = schemes;
            _portal = portal;
            _transferProtector = protection.CreateProtector("YoMi.LegacyTransfer.v1").ToTimeLimitedDataProtector();
        }

        private bool AllowAttempt(string email = null)
        {
            var allowed = _attempts.Allow("ip:" + HttpContext.Connection.RemoteIpAddress, 60);
            if (allowed && !string.IsNullOrEmpty(email)) allowed = _attempts.Allow("email:" + email.Trim().ToLowerInvariant(), 10);
            if (!allowed)
            {
                Response.StatusCode = 429;
                Response.Headers["Retry-After"] = "300";
                ModelState.AddModelError(string.Empty, "操作次數過多，請 5 分鐘後再試。");
            }
            return allowed;
        }

        private IActionResult Screen(string step, EmailEntryViewModel model)
        {
            ViewData["AuthStep"] = step;
            return View("~/Views/Account/Email.cshtml", model);
        }

        private IActionResult Finish(string returnUrl) => Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect("/");

        [AllowAnonymous, HttpPost("check-email"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Continue(EmailEntryViewModel model)
        {
            if (User.Identity?.IsAuthenticated == true) return LocalRedirect("/my-records");
            if (!ModelState.IsValid || !AllowAttempt()) return Screen("email", model);
            try
            {
                model.Email = FrontendMemberPassword.NormalizeEmail(model.Email);
                ModelState.Remove(nameof(model.Email));
                var name = await _login.GetPasswordMemberNameAsync(model.Email);
                ViewData["LoginName"] = name;
                return Screen(name != null ? "password" : "transfer", model);
            }
            catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
            catch (Exception ex) { Failure(ex); }
            return Screen("email", model);
        }

        [AllowAnonymous, HttpPost("login"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(AccountViewModel model)
        {
            if (ModelState.IsValid && AllowAttempt(model.Email))
            {
                try
                {
                    var member = await _login.SignInAsync(model.Email, model.Password);
                    await AuthCookie.SignInAsync(HttpContext, member, "password");
                    return Finish(model.ReturnUrl);
                }
                catch (UnauthorizedAccessException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
                catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
                catch (Exception ex) { Failure(ex); }
            }
            try { ViewData["LoginName"] = await _login.GetPasswordMemberNameAsync(model.Email); }
            catch (ArgumentException) { }
            catch (Exception ex) { Failure(ex); }
            return Screen("password", model);
        }

        [AllowAnonymous, HttpPost("register/start"), ValidateAntiForgeryToken]
        public IActionResult Registration(EmailEntryViewModel model) => Screen(ModelState.IsValid ? "register" : "email", model);

        [AllowAnonymous, HttpPost("register"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterViewModel model)
        {
            if (ModelState.IsValid && AllowAttempt(model.Email))
            {
                try
                {
                    var member = await _login.RegisterAsync(model.Email, model.Name, model.Password, model.ReferralCode);
                    await AuthCookie.SignInAsync(HttpContext, member, "password");
                    return Finish(model.ReturnUrl);
                }
                catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
                catch (DbUpdateException ex) { Failure(ex, "註冊未完成；此 Email 可能已有帳號，請返回登入或稍後重試。"); }
                catch (Exception ex) { Failure(ex); }
            }
            ViewData["RegistrationName"] = model.Name;
            return Screen("register", new EmailEntryViewModel { Email = model.Email, ReturnUrl = model.ReturnUrl, ReferralCode = model.ReferralCode });
        }

        [AllowAnonymous, HttpPost("transfer"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Transfer(TransferViewModel model)
        {
            if (!ModelState.IsValid || !AllowAttempt()) return Screen("transfer", model);
            try
            {
                var member = await _login.FindLegacyTransferAsync(model.LegacyId);
                if (member != null)
                    ViewData["TransferVip"] = (await _portal.GetVipStatusAsync(member.Id)).Tier.Name;
                return TransferScreen(new TransferTicket
                {
                    RequestId = Guid.NewGuid(), LegacyId = model.LegacyId,
                    Email = FrontendMemberPassword.NormalizeEmail(model.Email), ReturnUrl = model.ReturnUrl, ReferralCode = model.ReferralCode,
                    MemberName = member?.Name, OriginalEmail = member?.Email, OriginalUpdatedAt = member?.UpdatedAt ?? default,
                    Stage = member == null ? "support" : "confirm"
                });
            }
            catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
            catch (Exception ex) { Failure(ex); }
            return Screen("transfer", model);
        }

        [AllowAnonymous, HttpPost("transfer/confirm"), ValidateAntiForgeryToken]
        public IActionResult ConfirmTransfer(string transferToken)
        {
            var ticket = ReadTransfer(transferToken, "confirm");
            if (ticket == null) return InvalidTransfer();
            ticket.Stage = "password";
            return TransferScreen(ticket);
        }

        [AllowAnonymous, HttpPost("transfer/support/start"), ValidateAntiForgeryToken]
        public IActionResult TransferSupport(string transferToken)
        {
            var ticket = ReadTransfer(transferToken, "confirm");
            if (ticket == null) return InvalidTransfer();
            ticket.Stage = "support";
            return TransferScreen(ticket);
        }

        [AllowAnonymous, HttpPost("transfer/complete"), ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteTransfer(TransferPasswordViewModel model)
        {
            var ticket = ReadTransfer(model.TransferToken, "password");
            if (ticket == null) return InvalidTransfer();
            if (ModelState.IsValid && AllowAttempt(ticket.Email))
            {
                try
                {
                    var member = await _login.CompleteLegacyTransferAsync(ticket.LegacyId, ticket.OriginalEmail,
                        ticket.OriginalUpdatedAt, ticket.Email, model.Password);
                    await AuthCookie.SignInAsync(HttpContext, member, "password");
                    return Finish(ticket.ReturnUrl);
                }
                catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
                catch (Exception ex) { Failure(ex); }
            }
            return TransferScreen(ticket);
        }

        [AllowAnonymous, HttpPost("transfer/support"), ValidateAntiForgeryToken, RequestSizeLimit(6 * 1024 * 1024)]
        public async Task<IActionResult> UploadTransferSupport(string transferToken, IFormFile screenshot)
        {
            var ticket = ReadTransfer(transferToken, "support");
            if (ticket == null) return InvalidTransfer();
            if (!AllowAttempt()) return TransferScreen(ticket);
            if (!_attempts.Allow("transfer-upload:" + HttpContext.Connection.RemoteIpAddress, 5))
            {
                Response.StatusCode = 429;
                Response.Headers["Retry-After"] = "300";
                ModelState.AddModelError(string.Empty, "上傳次數過多，請 5 分鐘後再試。");
                return TransferScreen(ticket);
            }
            try
            {
                if (screenshot == null || screenshot.Length == 0 || screenshot.Length > 5 * 1024 * 1024)
                    throw new ArgumentException("請選擇 5 MB 以內的 JPG、PNG 或 WebP 舊站登入截圖。");
                using var buffer = new MemoryStream();
                await screenshot.CopyToAsync(buffer);
                await _login.SaveTransferSupportAsync(ticket.RequestId, ticket.LegacyId, ticket.Email, ticket.MemberName, buffer.ToArray());
                ticket.Stage = "submitted";
            }
            catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
            catch (Exception ex) { Failure(ex, "截圖未儲存，請稍後重試，或直接聯絡客服。"); }
            return TransferScreen(ticket);
        }

        private IActionResult TransferScreen(TransferTicket ticket)
        {
            ViewData["TransferToken"] = _transferProtector.Protect(JsonSerializer.Serialize(ticket), TimeSpan.FromMinutes(15));
            ViewData["TransferName"] = ticket.MemberName;
            ViewData["LegacyId"] = ticket.LegacyId;
            ViewData["SupportRequestId"] = ticket.RequestId;
            return Screen("transfer-" + ticket.Stage, new EmailEntryViewModel { Email = ticket.Email, ReturnUrl = ticket.ReturnUrl, ReferralCode = ticket.ReferralCode });
        }

        private TransferTicket ReadTransfer(string token, string stage)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 8192) return null;
            try
            {
                var ticket = JsonSerializer.Deserialize<TransferTicket>(_transferProtector.Unprotect(token));
                return ticket?.Stage == stage && ticket.LegacyId > 0 && ticket.RequestId != Guid.Empty ? ticket : null;
            }
            catch (CryptographicException) { return null; }
            catch (JsonException) { return null; }
        }

        private IActionResult InvalidTransfer()
        {
            TempData["Error"] = "轉移資料無效或已逾時，請重新輸入 Email 與轉移碼。";
            return LocalRedirect("/email-auth");
        }

        public sealed class TransferTicket
        {
            public Guid RequestId { get; set; }
            public int LegacyId { get; set; }
            public string Email { get; set; }
            public string ReturnUrl { get; set; }
            public string ReferralCode { get; set; }
            public string MemberName { get; set; }
            public string OriginalEmail { get; set; }
            public DateTime OriginalUpdatedAt { get; set; }
            public string Stage { get; set; }
        }

        [Authorize, HttpGet("password")]
        public IActionResult Password() => View("~/Views/Account/Password.cshtml", new PasswordSetupViewModel());

        [Authorize, HttpPost("password"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Password(PasswordSetupViewModel model)
        {
            if (ModelState.IsValid && AllowAttempt())
            {
                try
                {
                    await _login.SetInitialPasswordAsync(AuthCookie.GetMemberId(User), User.FindFirstValue("google_sub"), model.Password);
                    TempData["Success"] = "密碼已設定，下次可使用相同 Email 搭配密碼或 Google 登入。";
                    return LocalRedirect("/my-records");
                }
                catch (UnauthorizedAccessException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
                catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
                catch (Exception ex) { Failure(ex); }
            }
            model.Password = model.ConfirmPassword = null;
            return View("~/Views/Account/Password.cshtml", model);
        }

        [AllowAnonymous, HttpGet("link-google")]
        public async Task<IActionResult> GoogleLink()
        {
            if (await _schemes.GetSchemeAsync(Startup.GoogleExternalCookieScheme) == null) return LocalRedirect("/email-auth");
            var external = await HttpContext.AuthenticateAsync(Startup.GoogleExternalCookieScheme);
            if (!IsMemberLogin(external)) return LocalRedirect("/email-auth");
            return Screen("google-link", new EmailEntryViewModel { Email = external.Principal.FindFirstValue("email") });
        }

        [AllowAnonymous, HttpPost("link-google"), ValidateAntiForgeryToken]
        public async Task<IActionResult> GoogleLink(string password)
        {
            if (await _schemes.GetSchemeAsync(Startup.GoogleExternalCookieScheme) == null) return LocalRedirect("/email-auth");
            var external = await HttpContext.AuthenticateAsync(Startup.GoogleExternalCookieScheme);
            if (!IsMemberLogin(external)) return LocalRedirect("/email-auth");
            var email = external.Principal.FindFirstValue("email");
            if (AllowAttempt(email))
            {
                try
                {
                    var member = await _google.SignInAsync(external.Principal, password: password);
                    await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
                    await AuthCookie.SignInAsync(HttpContext, member, "google");
                    external.Properties.Items.TryGetValue("ReturnUrl", out var returnUrl);
                    return Finish(returnUrl);
                }
                catch (UnauthorizedAccessException) { ModelState.AddModelError(string.Empty, "密碼不正確或無法連結此帳號，請再試一次。"); }
                catch (Exception ex) { Failure(ex); }
            }
            return Screen("google-link", new EmailEntryViewModel { Email = email });
        }

        private static bool IsMemberLogin(AuthenticateResult external) => external.Succeeded &&
            external.Properties.Items.TryGetValue("Purpose", out var purpose) && purpose == "member";

        private void Failure(Exception error, string message = "暫時無法完成操作，請稍後再試。")
        {
            _logger.LogError(error, "Email account operation failed.");
            ModelState.AddModelError(string.Empty, message);
        }
    }
}
