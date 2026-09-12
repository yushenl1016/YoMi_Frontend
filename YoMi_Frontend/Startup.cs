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
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;
using YoMi_Frontend.Models;
using YoMi_Frontend.Services;

namespace YoMi_Frontend
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllersWithViews();
            services.AddDbContext<DBContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("DefaultConnection")));
            services.AddScoped<PortalService>();
            services.AddScoped<IPasswordHasher<FrontendMember>, PasswordHasher<FrontendMember>>();

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
                        var claimsMatch = member != null &&
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
            services.AddAuthorization();
            services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = 50 * 1024 * 1024;
            });

            VipRules.SelfCheck();
            VideoUrlParser.SelfCheck();
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
