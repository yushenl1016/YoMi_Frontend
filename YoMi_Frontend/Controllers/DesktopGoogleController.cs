using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YoMi_Frontend.Services;

namespace YoMi_Frontend.Controllers
{
    [ApiController, AllowAnonymous, Route("api/desktop/auth"), RequestSizeLimit(8192)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public sealed class DesktopGoogleController : ControllerBase
    {
        private readonly DesktopGoogleExchange _exchange;
        private readonly DesktopGoogleVerifier _google;
        public DesktopGoogleController(DesktopGoogleExchange exchange, DesktopGoogleVerifier google) { _exchange = exchange; _google = google; }
        public sealed class ExchangeInput { public string Code { get; set; } public string CodeVerifier { get; set; } public string RedirectUri { get; set; } }
        [HttpGet("config")]
        public IActionResult Config()
        {
            if (!Request.IsHttps) return BadRequest();
            try { return Ok(new { clientId = _exchange.ClientId() }); }
            catch { return StatusCode(503, new { error = "登入服務尚未設定，請聯絡管理員。" }); }
        }
        [HttpPost("exchange")]
        public async Task<IActionResult> Exchange([FromBody] ExchangeInput input)
        {
            Response.Headers.CacheControl = "no-store";
            if (!Request.IsHttps) return BadRequest();
            if (!_google.AllowAttempt(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")) return StatusCode(429, new { error = "登入嘗試過於頻繁，請稍後重試。" });
            try { return Ok(new { idToken = await _exchange.Exchange(input.Code, input.CodeVerifier, input.RedirectUri, HttpContext.RequestAborted) }); }
            catch (ArgumentException) { return BadRequest(new { error = "登入請求無效，請重新登入。" }); }
            catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Google 授權已失效，請重新登入。" }); }
            catch { return StatusCode(503, new { error = "Google 登入服務暫時無法使用，請稍後再試。" }); }
        }
    }
}
