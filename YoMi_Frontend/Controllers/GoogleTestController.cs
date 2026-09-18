using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;

namespace YoMi_Frontend.Controllers
{
    [AllowAnonymous, Route("account/google-test")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public class GoogleTestController : Controller
    {
        private readonly IWebHostEnvironment _environment;
        private readonly IAuthenticationSchemeProvider _schemes;

        public GoogleTestController(IWebHostEnvironment environment, IAuthenticationSchemeProvider schemes)
        {
            _environment = environment;
            _schemes = schemes;
        }

        [HttpGet("")]
        public IActionResult Index()
        {
            if (!_environment.IsDevelopment()) return NotFound();
            ViewData["Error"] = Request.Query.ContainsKey("error");
            return View();
        }

        [HttpPost("start"), ValidateAntiForgeryToken]
        public async Task<IActionResult> Start()
        {
            if (!_environment.IsDevelopment()) return NotFound();
            if (await _schemes.GetSchemeAsync(Startup.GoogleScheme) == null) return StatusCode(503);
            await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
            var properties = new AuthenticationProperties
            {
                RedirectUri = "/account/google-test/result",
                IsPersistent = false
            };
            properties.Items["Purpose"] = "test";
            return Challenge(properties, Startup.GoogleScheme);
        }

        [HttpGet("result")]
        public async Task<IActionResult> Result()
        {
            if (!_environment.IsDevelopment()) return NotFound();
            if (await _schemes.GetSchemeAsync(Startup.GoogleScheme) == null) return RedirectToAction(nameof(Index));
            var result = await HttpContext.AuthenticateAsync(Startup.GoogleExternalCookieScheme);
            await HttpContext.SignOutAsync(Startup.GoogleExternalCookieScheme);
            if (!result.Succeeded || !result.Properties.Items.TryGetValue("Purpose", out var purpose) || purpose != "test")
                return RedirectToAction(nameof(Index));
            return View("Index", result.Principal);
        }
    }
}
