using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Services;

namespace YoMi_Frontend
{
    public class Startup
    {
        internal const string GoogleScheme = "Google";
        internal const string GoogleExternalCookieScheme = "GoogleExternal";

        private readonly IWebHostEnvironment _environment;

        public Startup(IConfiguration configuration, IWebHostEnvironment environment)
        {
            Configuration = configuration;
            _environment = environment;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllersWithViews();
            services.AddDbContext<DBContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));
            services.AddScoped<PortalService>();
            services.AddScoped<YoMi_Admin.Library.Models.Orders.CustomerOrderService>();
            services.AddScoped<GoogleMemberLogin>();
            // Legacy password login/registration retained in AccountController but disabled.
            // services.AddScoped<IPasswordHasher<FrontendMember>, PasswordHasher<FrontendMember>>();

            var authentication = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "YoMi_Frontend.Auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.LoginPath = "/email-auth";
                    options.AccessDeniedPath = "/account/access-denied";
                    options.ExpireTimeSpan = TimeSpan.FromDays(365);
                    options.SlidingExpiration = true;
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        var database = context.HttpContext.RequestServices.GetRequiredService<DBContext>();
                        var member = int.TryParse(idValue, out var id)
                            ? await database.FrontendMember.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id)
                            : null;
                        var claimsMatch = member != null && member.HasLoggedInNewSite &&
                            !string.IsNullOrWhiteSpace(member.PasswordHash) &&
                            string.Equals(context.Principal.FindFirstValue("google_sub"), member.PasswordHash, StringComparison.Ordinal) &&
                            context.Principal.IsInRole(member.Role) &&
                            context.Principal.FindFirstValue(ClaimTypes.Name) == member.Name &&
                            context.Principal.FindFirstValue(ClaimTypes.Email) == member.Email;
                        if (!claimsMatch)
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    };
                });

            var credentialsSetting = Configuration["Authentication:Google:CredentialsPath"];
            var credentialsPath = string.IsNullOrWhiteSpace(credentialsSetting) ? null :
                Path.GetFullPath(credentialsSetting, _environment.ContentRootPath);
            var webRoot = Path.GetFullPath(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"));
            if (credentialsPath != null && credentialsPath.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Google credentials must be stored outside wwwroot.");
            if (credentialsPath != null && File.Exists(credentialsPath))
            {
                authentication.AddCookie(GoogleExternalCookieScheme, options =>
                {
                    options.Cookie.Name = "YoMi_Frontend.GoogleExternal";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                    options.SlidingExpiration = false;
                });
                authentication.AddOpenIdConnect(GoogleScheme, options =>
                {
                    using var credentials = JsonDocument.Parse(File.ReadAllText(credentialsPath));
                    var web = credentials.RootElement.GetProperty("web");
                    options.ClientId = web.GetProperty("client_id").GetString();
                    options.ClientSecret = web.GetProperty("client_secret").GetString();
                    options.Authority = "https://accounts.google.com";
                    options.SignInScheme = GoogleExternalCookieScheme;
                    options.CallbackPath = Configuration["Authentication:Google:CallbackPath"] ?? "/signin-google";
                    options.ResponseType = "code";
                    options.ResponseMode = "query";
                    options.UsePkce = true;
                    options.MapInboundClaims = false;
                    options.SaveTokens = false;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("email");
                    options.Events.OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.Prompt = "select_account";
                        return Task.CompletedTask;
                    };
                    options.Events.OnTokenValidated = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Principal?.FindFirstValue("sub")) ||
                            string.IsNullOrWhiteSpace(context.Principal?.FindFirstValue("email")))
                        {
                            context.Fail("Google did not return the required identity claims.");
                        }
                        return Task.CompletedTask;
                    };
                    options.Events.OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        var testOnly = context.Properties?.Items.TryGetValue("Purpose", out var purpose) == true && purpose == "test";
                        context.Response.Redirect(testOnly ? "/account/google-test?error=1" : "/email-auth?googleError=1");
                        return Task.CompletedTask;
                    };
                });
            }
            services.AddAuthorization();
            services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
            });

            VipRules.SelfCheck();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await next();
            });

            app.UseHttpsRedirection();
            var backgroundPath = Configuration["FrontendAssets:PhysicalPath"];
            if (!string.IsNullOrWhiteSpace(backgroundPath))
            {
                var physicalPath = Path.GetFullPath(backgroundPath, env.ContentRootPath);
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(physicalPath),
                    RequestPath = "/uploads/backgrounds"
                });
            }
            app.UseStaticFiles();
            app.UseStatusCodePagesWithReExecute("/status/{0}");
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");
            });

            Directory.CreateDirectory(Path.Combine(env.WebRootPath, "uploads", "backgrounds"));
        }
    }
}
