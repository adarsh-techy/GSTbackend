using GSTAutoPilot.Domain.Entities;

namespace GSTAutoPilot.Application.Services;

// Pure age/lifecycle math for an IRN. AcknowledgementDate is stored in UTC, so
// all comparisons use UtcNow (the spec's DateTime.Now would be wrong against
// UTC-stored ack dates).
public static class IRNAgeService
{
    public const double WindowHours = 24.0;

    public static bool IsCancellable(DateTime ackUtc)
        => (DateTime.UtcNow - ackUtc).TotalHours < WindowHours;

    public static double AgeHours(DateTime ackUtc)
        => Math.Max(0, (DateTime.UtcNow - ackUtc).TotalHours);

    // The lifecycle status shown to the UI: Cancelled wins; otherwise
    // Cancellable while inside the 24h window, else Locked.
    public static string GetLifecycleStatus(string storedStatus, DateTime ackUtc)
    {
        if (storedStatus == IRNStatus.Cancelled) return IRNStatus.Cancelled;
        return IsCancellable(ackUtc) ? IRNStatus.Cancellable : IRNStatus.Locked;
    }

    public static string GetTimeRemaining(DateTime ackUtc)
    {
        var remaining = WindowHours - (DateTime.UtcNow - ackUtc).TotalHours;
        return remaining > 0
            ? $"{remaining:F1} hrs remaining"
            : "Cancellation window closed";
    }
}
