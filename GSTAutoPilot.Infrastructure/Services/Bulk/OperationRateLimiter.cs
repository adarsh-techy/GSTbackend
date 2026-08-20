using System.Collections.Concurrent;

namespace GSTAutoPilot.Infrastructure.Services.Bulk;

// Server-side rate limits for the operations that reach outside the app: IRN
// generation hits the NIC e-Invoice portal through WhiteBooks, and e-Invoice
// email goes out over the tenant's SMTP. Bulk runs are driven from the browser,
// so pacing them client-side would be a suggestion; enforcing it here makes it
// a limit, and the same ceiling then also covers someone clicking fast by hand.
//
// In-memory and per-process: a single API instance is what this deployment runs,
// and a limiter that forgets on restart fails safe (it delays less, never more).
public class OperationRateLimiter
{
    // Sliding window of recent grant timestamps, keyed by tenant + operation.
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly TimeProvider _time;

    public OperationRateLimiter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    // Named limits. Values are the documented ceilings from the e-Invoice /
    // SMTP side, not guesses; change them here rather than at the call sites.
    public static readonly OperationLimit EInvoiceGenerate = new("einvoice.generate", 10, TimeSpan.FromMinutes(1));
    public static readonly OperationLimit EInvoiceEmail = new("einvoice.email", 30, TimeSpan.FromHours(1));

    // Takes a slot if one is free. When it isn't, `retryAfter` says how long
    // until the oldest call in the window falls out of it.
    public bool TryAcquire(Guid tenantId, OperationLimit limit, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        var window = _windows.GetOrAdd($"{tenantId}|{limit.Name}", _ => new Window());

        lock (window.Gate)
        {
            while (window.Grants.Count > 0 && now - window.Grants.Peek() >= limit.Period)
                window.Grants.Dequeue();

            if (window.Grants.Count < limit.Max)
            {
                window.Grants.Enqueue(now);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            // Round up: reporting 0s to a caller that must wait invites a
            // pointless immediate retry.
            var wait = limit.Period - (now - window.Grants.Peek());
            retryAfter = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            return false;
        }
    }

    // Slots left in the current window — for showing a bulk run's headroom
    // before it starts. Does not consume anything.
    public int Remaining(Guid tenantId, OperationLimit limit)
    {
        var now = _time.GetUtcNow();
        if (!_windows.TryGetValue($"{tenantId}|{limit.Name}", out var window)) return limit.Max;
        lock (window.Gate)
        {
            var live = window.Grants.Count(g => now - g < limit.Period);
            return Math.Max(0, limit.Max - live);
        }
    }

    private sealed class Window
    {
        public readonly object Gate = new();
        public readonly Queue<DateTimeOffset> Grants = new();
    }
}

public sealed record OperationLimit(string Name, int Max, TimeSpan Period);
