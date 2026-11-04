using Discord;

namespace SlopSniper.Services;

/// <summary>When something last happened, for /sniper status. Safe to use from any thread.</summary>
public sealed class LastSeen(TimeProvider time)
{
    private long _ticks;

    public DateTimeOffset? At
    {
        get
        {
            var ticks = Interlocked.Read(ref _ticks);
            return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
    }

    public void Mark() => Interlocked.Exchange(ref _ticks, time.GetUtcNow().UtcTicks);

    /// <summary>"5 minutes ago" as a Discord timestamp, or "never".</summary>
    public string Describe() => At is { } at ? TimestampTag.FormatFromDateTimeOffset(at, TimestampTagStyles.Relative) : "never";
}
