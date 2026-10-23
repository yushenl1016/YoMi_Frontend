using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

        private static string PasswordStamp(FrontendMember member) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(member.EmailPasswordHash ?? "")));

        public static bool Matches(ClaimsPrincipal principal, FrontendMember member)
        {
            if (principal?.Identity?.IsAuthenticated != true || member == null || !member.HasLoggedInNewSite ||
                GetMemberId(principal) != member.Id || !principal.IsInRole(member.Role) ||
                principal.FindFirstValue(ClaimTypes.Name) != member.Name || principal.FindFirstValue(ClaimTypes.Email) != member.Email)
                return false;
            return principal.FindFirstValue("auth_method") == "password"
                ? !string.IsNullOrEmpty(member.EmailPasswordHash) && principal.FindFirstValue("password_stamp") == PasswordStamp(member)
                : !string.IsNullOrEmpty(member.PasswordHash) && principal.FindFirstValue("google_sub") == member.PasswordHash;
        }

        public static Task SignInAsync(HttpContext context, FrontendMember member, string method = null)
        {
            method ??= context.User.FindFirstValue("auth_method") ?? "google";
            if (method != "google" && method != "password") throw new ArgumentException("Unknown authentication method.");
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, member.Id.ToString()),
                new(ClaimTypes.Name, member.Name),
                new(ClaimTypes.Email, member.Email),
                new(ClaimTypes.Role, member.Role),
                new("auth_method", method),
                method == "password" ? new("password_stamp", PasswordStamp(member)) : new("google_sub", member.PasswordHash)
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
