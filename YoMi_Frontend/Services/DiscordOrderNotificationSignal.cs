using System;
using System.Threading;
using System.Threading.Tasks;

namespace YoMi_Frontend.Services
{
    public sealed class DiscordOrderNotificationSignal : IDisposable
    {
        private readonly SemaphoreSlim _pending = new(0, 1);

        public void Notify()
        {
            try { _pending.Release(); }
            catch (SemaphoreFullException) { } // One pending wake-up covers all orders in the durable queue.
        }

        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _pending.WaitAsync(timeout, cancellationToken);

        public void Dispose() => _pending.Dispose();
    }
}
