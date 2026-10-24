using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Frontend.Services;

namespace YoMi_Frontend.Controllers
{
    [ApiController, AllowAnonymous, Route("api/desktop"), RequestSizeLimit(1048576)]
    public sealed class DesktopController : ControllerBase
    {
        private readonly DesktopGoogleVerifier _google;
        private readonly GoogleMemberLogin _members;
        private readonly DesktopPetStore _store;
        public DesktopController(DesktopGoogleVerifier google, GoogleMemberLogin members, DesktopPetStore store) { _google = google; _members = members; _store = store; }
        public sealed class GoogleInput { public string IdToken { get; set; } }
        public sealed class DocumentInput { public JsonElement Payload { get; set; } public long Revision { get; set; } }
        private string Bearer => Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? Request.Headers.Authorization.ToString().Substring(7) : null;
        private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
        {
            Response.Headers.CacheControl = "no-store";
            if (!Request.IsHttps) return BadRequest(new { error = "HTTPS is required." });
            try { return await action(); }
            catch (UnauthorizedAccessException) { return Unauthorized(new { error = "登入驗證失敗，請確認前台會員的 Google 帳號綁定。" }); }
            catch (SecurityTokenException) { return Unauthorized(new { error = "Google 驗證已失效，請重新登入。" }); }
            catch (DesktopEconomyConflictException error) { return Conflict(new { error = error.Message, code = error.Code }); }
            catch (ArgumentException) { return BadRequest(new { error = "存檔格式不正確，原資料已保留。" }); }
            catch (Exception) { return StatusCode(503, new { error = "同步服務暫時無法使用，進度仍保存在這台電腦。" }); }
        }
        [HttpPost("auth/google")]
        public Task<IActionResult> Google([FromBody] GoogleInput input) => Run(async () =>
        {
            if (!_google.AllowAttempt(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")) return StatusCode(429, new { error = "登入嘗試過於頻繁，請稍後重試。" });
            var principal = await _google.VerifyAsync(input.IdToken, HttpContext.RequestAborted);
            var member = await _members.SignInAsync(principal, allowLegacyEmailBinding: false);
            return Ok(await _store.CreateSession(member.Id, principal.FindFirstValue("sub")));
        });
        [HttpPost("auth/refresh")]
        public Task<IActionResult> Refresh() => Run(async () =>
        {
            var session = await _store.RefreshSession(Bearer);
            if (session == null) return Unauthorized(new { error = "登入已到期或失效，請重新使用 Google 登入。" });
            return Ok(session);
        });
        [HttpGet("documents")]
        public Task<IActionResult> Documents() => Run(async () =>
        {
            var member = await _store.Member(Bearer);
            if (!member.HasValue) return Unauthorized(new { error = "請重新登入以繼續同步。" });
            return Ok(new { memberId = member.Value, documents = await _store.Documents(member.Value) });
        });
        [HttpPut("documents/{kind}")]
        public Task<IActionResult> Put(string kind, [FromBody] DocumentInput input) => Run(async () =>
        {
            var member = await _store.Member(Bearer);
            if (!member.HasValue) return Unauthorized(new { error = "請重新登入以繼續同步。" });
            var revision = await _store.Put(member.Value, kind, input.Payload, input.Revision);
            if (!revision.HasValue) return Conflict(new { error = "伺服器已有較新的進度，兩份資料皆已保留。" });
            return Ok(new { revision = revision.Value });
        });
        [HttpPost("auth/logout")]
        public Task<IActionResult> Logout() => Run(async () =>
        {
            var member = await _store.Member(Bearer);
            if (!member.HasValue) return Unauthorized();
            await _store.Revoke(Bearer); return Ok(new { ok = true });
        });
    }
}
