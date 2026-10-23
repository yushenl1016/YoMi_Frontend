using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace YoMi_Frontend.Services
{
    public class DiscordOrderNotificationHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<DiscordOrderNotificationHostedService> _logger;
        public DiscordOrderNotificationHostedService(IServiceScopeFactory scopes, ILogger<DiscordOrderNotificationHostedService> logger)
        { _scopes = scopes; _logger = logger; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<DiscordOrderNotificationService>().ProcessAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("Discord notification worker failed ({ErrorType}); it will retry.", ex.GetType().Name);
                    try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                }
            }
        }
    }
}
