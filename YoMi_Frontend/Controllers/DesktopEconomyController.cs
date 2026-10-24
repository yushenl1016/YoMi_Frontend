using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Frontend.Services;

namespace YoMi_Frontend.Controllers
{
    [ApiController,AllowAnonymous,Route("api/desktop/economy"),RequestSizeLimit(1048576)]
    public sealed class DesktopEconomyController : ControllerBase
    {
        readonly DesktopPetStore store; readonly DesktopPetOperations operations;
        public DesktopEconomyController(DesktopPetStore store,DesktopPetOperations operations) { this.store=store; this.operations=operations; }
        public sealed class BootstrapInput { public JsonElement InitialProfile { get; set; } public string PendingFocusStartedAt { get; set; } }
        public sealed class OperationInput { public string OperationId { get; set; } public string Type { get; set; } public JsonElement Payload { get; set; } }
        async Task<IActionResult> Run(Func<int,Task<object>> action)
        {
            Response.Headers.CacheControl="no-store";
            if(!Request.IsHttps) return BadRequest(new {error="HTTPS is required."});
            try
            {
                var header=Request.Headers.Authorization.ToString(); var member=await store.Member(header.StartsWith("Bearer ",StringComparison.Ordinal)?header.Substring(7):null);
                if(!member.HasValue) return Unauthorized(new { error="請先使用 Google 登入。" });
                return Ok(await action(member.Value));
            }
            catch(DesktopEconomyConflictException ex) { return Conflict(new { ok=false,error=ex.Message,code=ex.Code }); }
            catch(ArgumentException ex) { return BadRequest(new { ok=false,error=ex.Message,code="invalid_operation" }); }
            catch(Exception) { return StatusCode(503,new { ok=false,error="結算服務暫時無法使用，請保留原操作稍後重試。",code="temporarily_unavailable" }); }
        }
        [HttpPost("bootstrap")] public Task<IActionResult> Bootstrap([FromBody] BootstrapInput input)=>Run(member=>operations.Bootstrap(member,input.InitialProfile,input.PendingFocusStartedAt));
        [HttpGet("state")] public Task<IActionResult> State()=>Run(operations.State);
        [HttpPost("operate")] public Task<IActionResult> Operate([FromBody] OperationInput input)=>Run(member=>operations.Operate(member,input.OperationId,input.Type,input.Payload));
    }
}

