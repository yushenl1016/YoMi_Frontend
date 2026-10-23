using Microsoft.Extensions.Caching.Memory;
using System;

namespace YoMi_Frontend.Services
{
    public sealed class AccountAttemptLimiter : IDisposable
    {
        // shortcut: process-local limits; use a shared store when deploying multiple instances.
        private readonly MemoryCache _attempts = new(new MemoryCacheOptions { SizeLimit = 10000 });
        private readonly object _gate = new();

        public bool Allow(string key, int limit)
        {
            lock (_gate)
            {
                if (!_attempts.TryGetValue(key, out Counter counter))
                {
                    if (_attempts.Count >= 10000) return false;
                    counter = new Counter();
                    _attempts.Set(key, counter, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
                }
                return ++counter.Attempts <= limit;
            }
        }

        private sealed class Counter { public int Attempts; }
        public void Dispose() => _attempts.Dispose();
    }
}
