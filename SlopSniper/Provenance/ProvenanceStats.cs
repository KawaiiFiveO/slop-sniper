namespace SlopSniper.Provenance;

/// <summary>Automatic scanning counters since the bot started, shown by /sniper status. Manual checks aren't counted.</summary>
public sealed class ProvenanceStats(TimeProvider time)
{
    private long _messagesQueued;
    private long _messagesDropped;
    private long _imagesChecked;
    private long _cacheHits;
    private long _imagesDetected;
    private long _imagesSkipped;
    private long _imagesFailed;

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public long MessagesQueued => Interlocked.Read(ref _messagesQueued);
    public long MessagesDropped => Interlocked.Read(ref _messagesDropped);

    /// <summary>Images with a result, including those answered from the cache.</summary>
    public long ImagesChecked => Interlocked.Read(ref _imagesChecked);

    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long ImagesDetected => Interlocked.Read(ref _imagesDetected);

    /// <summary>Too large or an unsupported type.</summary>
    public long ImagesSkipped => Interlocked.Read(ref _imagesSkipped);

    public long ImagesFailed => Interlocked.Read(ref _imagesFailed);

    public void MessageQueued() => Interlocked.Increment(ref _messagesQueued);
    public void MessageDropped() => Interlocked.Increment(ref _messagesDropped);

    public void Record(ImageCheck check)
    {
        switch (check.Status)
        {
            case CheckStatus.Checked:
                Interlocked.Increment(ref _imagesChecked);
                if (check.FromCache)
                {
                    Interlocked.Increment(ref _cacheHits);
                }
                if (check.Detected)
                {
                    Interlocked.Increment(ref _imagesDetected);
                }
                break;
            case CheckStatus.TooLarge or CheckStatus.Unsupported:
                Interlocked.Increment(ref _imagesSkipped);
                break;
            case CheckStatus.Failed:
                Interlocked.Increment(ref _imagesFailed);
                break;
        }
    }
}
