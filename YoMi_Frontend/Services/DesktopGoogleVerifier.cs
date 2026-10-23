using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace YoMi_Frontend.Services
{
    public sealed class DesktopGoogleVerifier
    {
        private readonly string[] _audiences;
        private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata = new(
            "https://accounts.google.com/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
        private readonly MemoryCache _attempts = new(new MemoryCacheOptions { SizeLimit = 2048 });
        private readonly object _gate = new();
        public DesktopGoogleVerifier(IConfiguration config) => _audiences = config.GetSection("DesktopPet:GoogleClientIds").Get<string[]>() ?? Array.Empty<string>();

        public bool AllowAttempt(string address)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (!_attempts.TryGetValue(address, out Attempt value))
                {
                    value = new Attempt { Until = now.AddMinutes(1) };
                    _attempts.Set(address, value, new MemoryCacheEntryOptions { AbsoluteExpiration = value.Until, Size = 1 });
                    if (!_attempts.TryGetValue(address, out _)) return false;
                }
                return ++value.Count <= 10;
            }
        }
        private sealed class Attempt { public int Count; public DateTime Until; }

        public async Task<ClaimsPrincipal> VerifyAsync(string token, CancellationToken cancellation)
        {
            if (_audiences.Length == 0) throw new InvalidOperationException("Desktop Google audience is not configured.");
            if (string.IsNullOrEmpty(token) || token.Length > 16384) throw new UnauthorizedAccessException();
            var metadata = await _metadata.GetConfigurationAsync(cancellation);
            try { return ValidateWithKeys(token, metadata.SigningKeys, _audiences); }
            catch (SecurityTokenSignatureKeyNotFoundException) { _metadata.RequestRefresh(); throw; }
        }

        public static ClaimsPrincipal ValidateWithKeys(string token, IEnumerable<SecurityKey> keys, IEnumerable<string> audiences)
        {
            var allowed = audiences.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            if (allowed.Length == 0 || string.IsNullOrEmpty(token) || token.Length > 16384) throw new UnauthorizedAccessException();
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuers = new[] { "https://accounts.google.com", "accounts.google.com" },
                ValidateAudience = true, ValidAudiences = allowed, ValidateLifetime = true,
                RequireExpirationTime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys, ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }, ClockSkew = TimeSpan.FromSeconds(30),
                AuthenticationType = "DesktopGoogle"
            }, out _);
            var sub = principal.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(sub) || sub.Length > 255 ||
                string.IsNullOrWhiteSpace(principal.FindFirstValue("email")) ||
                !string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException();
            return principal;
        }
    }
}
