using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace YoMi_Frontend.Services
{
    // Only the desktop client ID is public. The token exchange credential stays outside wwwroot.
    public sealed class DesktopGoogleExchange
    {
        private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        private readonly string _path;
        private readonly string[] _audiences;
        public DesktopGoogleExchange(IConfiguration config, IWebHostEnvironment environment)
        {
            _path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, config["DesktopPet:GoogleCredentialsPath"] ?? "../local-secrets/desktop-google.json"));
            var web = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
            if (_path.StartsWith(web + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Desktop OAuth credentials must be outside wwwroot.");
            _audiences = config.GetSection("DesktopPet:GoogleClientIds").Get<string[]>() ?? Array.Empty<string>();
        }
        private (string Id, string Secret) Credentials()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            if (json.RootElement.TryGetProperty("web", out _)) throw new InvalidOperationException("Desktop credentials required.");
            var installed = json.RootElement.GetProperty("installed");
            var id = installed.GetProperty("client_id").GetString();
            var secret = installed.GetProperty("client_secret").GetString();
            if (!_audiences.Contains(id, StringComparer.Ordinal) || !Regex.IsMatch(id ?? "", @"^[\w.-]+\.apps\.googleusercontent\.com$") || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Desktop OAuth configuration mismatch.");
            return (id, secret);
        }
        public string ClientId() => Credentials().Id;
        public static void Validate(string code, string verifier, string redirect)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 4096 || !Regex.IsMatch(verifier ?? "", @"^[A-Za-z0-9._~-]{43,128}$") ||
                !Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.Port < 1024 ||
                uri.AbsolutePath != "/oauth/callback" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Invalid desktop OAuth request.");
        }
        public async Task<string> Exchange(string code, string verifier, string redirect, CancellationToken cancellation)
        {
            Validate(code, verifier, redirect);
            var credential = Credentials();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"] = credential.Id, ["client_secret"] = credential.Secret, ["code"] = code,
                ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
            });
            using var response = await Http.PostAsync("https://oauth2.googleapis.com/token", form, cancellation);
            if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var token = json.RootElement.GetProperty("id_token").GetString();
            if (string.IsNullOrEmpty(token) || token.Length > 16384) throw new UnauthorizedAccessException();
            return token;
        }
    }
}
