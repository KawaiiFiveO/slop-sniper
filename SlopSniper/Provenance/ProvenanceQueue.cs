using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Provenance;

/// <summary>Holds messages waiting to be checked and works through them one at a time, so a burst of images is
/// spread out instead of hitting OpenAI's rate limit.</summary>
public sealed class ProvenanceQueue(
    ImageChecker checker,
    ProvenanceReporter reporter,
    OpenAiStatus status,
    ProvenanceStats stats,
    TimeProvider time,
    IOptionsMonitor<BotOptions> options,
    ILogger<ProvenanceQueue> logger) : BackgroundService
{
    private static readonly TimeSpan SuspendedPollInterval = TimeSpan.FromSeconds(30);

    // Unbounded so QueueCapacity can change on reload; TryEnqueue enforces it. Not SingleReader: that variant
    // doesn't support Count.
    private readonly Channel<ScanJob> _jobs = Channel.CreateUnbounded<ScanJob>();

    public int Count => _jobs.Reader.Count;

    /// <returns>False if the queue is full and the job was dropped.</returns>
    public bool TryEnqueue(ScanJob job) =>
        _jobs.Reader.Count < options.CurrentValue.Provenance.QueueCapacity && _jobs.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // While checks are suspended, jobs wait in the queue, and new ones are dropped once it's full.
                while (status.IsSuspended)
                {
                    await Task.Delay(SuspendedPollInterval, time, stoppingToken);
                }

                var job = await _jobs.Reader.ReadAsync(stoppingToken);
                try
                {
                    await ProcessAsync(job, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to scan message {MessageId} in {ChannelId}.", job.MessageId, job.ChannelId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; anything still queued is dropped.
        }
    }

    private async Task ProcessAsync(ScanJob job, CancellationToken cancellationToken)
    {
        List<ImageCheck> checks = [];
        foreach (var image in job.Images)
        {
            var check = status.IsSuspended
                ? new ImageCheck(image, CheckStatus.Failed, Detail: "OpenAI checks are suspended")
                : await checker.CheckAsync(image, cancellationToken);
            stats.Record(check);
            checks.Add(check);
        }

        var detected = checks.Where(c => c.Detected).ToList();
        if (detected.Count == 0)
        {
            logger.LogDebug("Message {MessageId} in {ChannelId}: nothing detected in {Count} images.", job.MessageId, job.ChannelId, checks.Count);
            return;
        }

        logger.LogInformation("Message {MessageId} in {ChannelId} by {UserId}: OpenAI provenance detected in {Detected} of {Count} images.",
            job.MessageId, job.ChannelId, job.AuthorId, detected.Count, checks.Count);
        await reporter.ReportDetectionAsync(job, detected);
    }
}
