using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Frontend.Services;

namespace YoMi_Frontend.Controllers
{
    [ApiController, AllowAnonymous, Route("api/desktop"), RequestSizeLimit(32768)]
    public sealed class DesktopCommunityController : ControllerBase
    {
        private readonly DesktopPetStore _store;
        private readonly DesktopAnnouncements _announcements;
        private readonly DesktopRooms _rooms;
        private readonly IServiceScopeFactory _scopes;
        public DesktopCommunityController(DesktopPetStore store, DesktopAnnouncements announcements, DesktopRooms rooms, IServiceScopeFactory scopes)
        { _store = store; _announcements = announcements; _rooms = rooms; _scopes = scopes; }
        private string Bearer => Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal) ? Request.Headers.Authorization.ToString().Substring(7) : null;
        [HttpGet("announcements")]
        public async Task<IActionResult> Announcements()
        {
            Response.Headers.CacheControl = "no-store";
            try { return Ok(await _announcements.ListAsync(Bearer == null ? null : await _store.Member(Bearer))); }
            catch (Exception) { return StatusCode(503, new { ok = false, error = "公告服務暫時無法使用，請稍後重試。" }); }
        }
        [HttpPost("announcements")]
        public Task<IActionResult> Create([FromBody] DesktopAnnouncements.Input input) => Write("create", 0, input);
        [HttpPut("announcements/{id:long}")]
        public Task<IActionResult> Update(long id, [FromBody] DesktopAnnouncements.Input input) => Write("update", id, input);
        [HttpDelete("announcements/{id:long}")]
        public Task<IActionResult> Delete(long id, [FromBody] DesktopAnnouncements.Input input) => Write("delete", id, input);
        private async Task<IActionResult> Write(string action, long id, DesktopAnnouncements.Input input)
        {
            Response.Headers.CacheControl = "no-store";
            if (!Request.IsHttps) return BadRequest(new { ok = false, error = "HTTPS is required." });
            try
            {
                var member = await _store.Member(Bearer);
                if (!member.HasValue) return Unauthorized(new { ok = false, error = "請先登入。" });
                return Ok(await _announcements.WriteAsync(member.Value, action, id, input));
            }
            catch (UnauthorizedAccessException) { return StatusCode(403, new { ok = false, error = "只有管理員可修改公告。" }); }
            catch (DesktopAnnouncements.ConflictException) { return Conflict(new { ok = false, error = "公告已有更新，請重新整理後再編輯。" }); }
            catch (KeyNotFoundException) { return NotFound(new { ok = false, error = "找不到這則公告。" }); }
            catch (ArgumentException error) { return BadRequest(new { ok = false, error = error.Message }); }
            catch (Exception) { return StatusCode(503, new { ok = false, error = "公告尚未儲存成功，請重新整理後重試。" }); }
        }
        [HttpGet("rooms")]
        public async Task Rooms()
        {
            if (!Request.IsHttps) { Response.StatusCode = 400; return; }
            var token = Bearer;
            int? member;
            try { member = await RoomMember(token); }
            catch (Exception) { Response.StatusCode = 503; return; }
            if (!member.HasValue) { Response.StatusCode = 401; return; }
            if (!HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = 400; return; }
            await _rooms.HandleAsync(HttpContext, member.Value, async () => await RoomMember(token) == member, async () =>
            {
                using var scope = _scopes.CreateScope();
                var documents = await scope.ServiceProvider.GetRequiredService<DesktopPetStore>().Documents(member.Value);
                if (!documents.TryGetValue("profile", out var profile)) return Array.Empty<string>();
                var document = JsonSerializer.SerializeToElement(profile);
                if (!document.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("inventory", out var inventory) || inventory.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
                return inventory.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray();
            });
        }
        private async Task<int?> RoomMember(string token)
        {
            // A socket may stay open for hours; release the database connection after each check.
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DesktopPetStore>().Member(token);
        }
    }
}
