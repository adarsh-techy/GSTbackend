using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace GSTAutoPilot.Infrastructure.Services;

// Short-lived in-process cache for expensive CarolERP reads (the per-tenant
// outward/inward stored procedures and the period scan).
//
// READ PATH ONLY. CarolERP is the customer's live ERP and this app never writes
// to it; this type exists purely to stop the SAME stored procedure running two
// to four times for a single page load.
//
// Two things matter for correctness:
//
//  * Every key MUST carry the tenant and the active company, otherwise one
//    tenant would be served another tenant's rows. Key building lives at the
//    call sites, which are the only places that know what a result depends on.
//
//  * Concurrent misses on one key are collapsed behind a per-key gate. Without
//    it the dashboard's parallel /gst-summary and /invoice/gstr1 calls both
//    miss and both execute the SP — which is the exact cost being removed.
public sealed class ErpQueryCache
{
    private readonly IMemoryCache _cache;

    // Bounded by the number of distinct (tenant, company, period) keys, so the
    // map stays small. Gates are deliberately never removed: evicting one while
    // another request is queued on it would let a second caller build a fresh
    // gate and run the factory concurrently, which is the thing we are here to
    // prevent.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public ErpQueryCache(IMemoryCache cache) => _cache = cache;

    public async Task<T> GetOrAddAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct)
    {
        // A non-positive TTL disables caching, so any of these can be turned
        // off from appsettings without a redeploy of behaviour.
        if (ttl <= TimeSpan.Zero) return await factory(ct);

        if (_cache.TryGetValue(key, out T? cached) && cached is not null) return cached;

        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check: the request we queued behind has very likely just
            // populated the entry.
            if (_cache.TryGetValue(key, out cached) && cached is not null) return cached;

            var fresh = await factory(ct);
            _cache.Set(key, fresh, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl,
            });
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }
}
