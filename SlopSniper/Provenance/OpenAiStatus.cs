using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.Provenance;

/// <summary>Tracks whether OpenAI checks are suspended after an error that retrying won't fix soon, such as a bad
/// key or exhausted quota. A successful check or a config reload lifts the suspension.</summary>
public sealed class OpenAiStatus : IDisposable
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly IDisposable? _reloadSubscription;

    public OpenAiStatus(TimeProvider time, IOptionsMonitor<BotOptions> options)
    {
        _time = time;

        // A reload may have fixed the key, so try again.
        _reloadSubscription = options.OnChange(_ => Clear());
    }

    /// <summary>Why checks are suspended, or null if they aren't.</summary>
    public string? SuspendedReason { get; private set; }

    /// <summary>When checks are tried again. Null while suspended means only a reload or restart retries.</summary>
    public DateTimeOffset? RetryAt { get; private set; }

    public DateTimeOffset? LastSuccess { get; private set; }

    public bool IsSuspended
    {
        get
        {
            lock (_lock)
            {
                return SuspendedReason is not null && (RetryAt is null || _time.GetUtcNow() < RetryAt);
            }
        }
    }

    /// <returns>True if checks weren't already suspended, so the caller should alert.</returns>
    public bool Suspend(string reason, TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            var isNew = SuspendedReason is null;
            SuspendedReason = reason;
            RetryAt = retryAfter is { } delay ? _time.GetUtcNow() + delay : null;
            return isNew;
        }
    }

    /// <returns>True if checks were suspended until now, so the caller can announce that they resumed.</returns>
    public bool ReportSuccess()
    {
        lock (_lock)
        {
            LastSuccess = _time.GetUtcNow();
            var wasSuspended = SuspendedReason is not null;
            SuspendedReason = null;
            RetryAt = null;
            return wasSuspended;
        }
    }

    private void Clear()
    {
        lock (_lock)
        {
            SuspendedReason = null;
            RetryAt = null;
        }
    }

    public void Dispose() => _reloadSubscription?.Dispose();
}
