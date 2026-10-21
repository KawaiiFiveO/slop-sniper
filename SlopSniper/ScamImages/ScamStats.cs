namespace SlopSniper.ScamImages;

/// <summary>Scam detection counters since the bot started, shown by /sniper status.</summary>
public sealed class ScamStats
{
    private long _detections;
    private long _followUps;
    private long _postsDeleted;
    private long _timeouts;
    private long _failures;
    private long _undone;
    private long _dropped;

    public long Detections => Interlocked.Read(ref _detections);

    /// <summary>More copies posted by an author who was already acted on.</summary>
    public long FollowUps => Interlocked.Read(ref _followUps);

    public long PostsDeleted => Interlocked.Read(ref _postsDeleted);
    public long Timeouts => Interlocked.Read(ref _timeouts);

    /// <summary>Detections where a delete or the timeout failed.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    public long Undone => Interlocked.Read(ref _undone);

    /// <summary>Jobs skipped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(ScamActions actions)
    {
        Interlocked.Increment(ref _detections);
        Interlocked.Add(ref _postsDeleted, actions.Deleted);
        if (actions.TimedOutFor is not null)
        {
            Interlocked.Increment(ref _timeouts);
        }
        if (actions.DeleteError is not null || actions.TimeoutError is not null)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    public void FollowUp(bool deleted)
    {
        Interlocked.Increment(ref _followUps);
        if (deleted)
        {
            Interlocked.Increment(ref _postsDeleted);
        }
    }

    public void Undo() => Interlocked.Increment(ref _undone);
    public void Drop() => Interlocked.Increment(ref _dropped);
}
