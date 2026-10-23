using Microsoft.Extensions.Configuration;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace YoMi_Frontend
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddJsonFile(Path.Combine(context.HostingEnvironment.ContentRootPath, "../local-secrets/desktop-api.json"), optional: true, reloadOnChange: false);
                    config.AddJsonFile(".secrets/discord-orders.json", optional: true, reloadOnChange: true);
                    config.AddEnvironmentVariables();
                    if (args.Length > 0) config.AddCommandLine(args);
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
