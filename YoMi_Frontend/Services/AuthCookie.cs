using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models.Frontend;

namespace YoMi_Frontend.Services
{
    public static class AuthCookie
    {
        public static int GetMemberId(ClaimsPrincipal user)
        {
            return int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
        }

        public static Task SignInAsync(HttpContext context, FrontendMember member)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, member.Id.ToString()),
                new(ClaimTypes.Name, member.Name),
                new(ClaimTypes.Email, member.Email),
                new(ClaimTypes.Role, member.Role)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365)
            });
        }
    }
}
