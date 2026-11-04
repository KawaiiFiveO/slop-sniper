using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>Works through scam jobs in two lanes. The fast lane carries out detections, deletes follow-ups and compares
/// posts with known scam images; the slow lane reads the text in suspicious images, one at a time. So acting never
/// waits behind OCR, and a post going to OCR has its hashes and evidence copy fetched first: anti-spam bots often ban
/// the author within a second or two, deleting the post and its images. Separate from the provenance queue, so it
/// never waits behind OpenAI's throttle.</summary>
public sealed class ScamQueue(
    DiscordSocketClient client,
    BurstTracker tracker,
    KnownScams known,
    ScamImageHasher hasher,
    OcrRunner ocr,
    ScamResponder responder,
    ScamModerator moderator,
    ScamStats stats,
    TimeProvider time,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamQueue> logger) : BackgroundService
{
    private const int Capacity = 100;

    // Each read holds its evidence copy, a few dozen KB, while it waits.
    private const int ReadCapacity = 20;

    private readonly Channel<ScamJob> _jobs = Channel.CreateUnbounded<ScamJob>();
    private readonly Channel<ScamRead> _reads = Channel.CreateUnbounded<ScamRead>();
    private readonly LastSeen _lastChecked = new(time);
    private bool _permissionsChecked;

    /// <summary>For /sniper status.</summary>
    public int Waiting => _jobs.Reader.Count;

    /// <summary>For /sniper status.</summary>
    public int WaitingToRead => _reads.Reader.Count;

    /// <summary>When the bot last finished with a post, for /sniper status.</summary>
    public LastSeen LastChecked => _lastChecked;

    public bool TryEnqueue(ScamJob job)
    {
        if (_jobs.Reader.Count < Capacity && _jobs.Writer.TryWrite(job))
        {
            return true;
        }
        stats.Drop();
        return false;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        client.Ready += OnReadyAsync;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        client.Ready -= OnReadyAsync;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await known.LoadAsync();
        await Task.WhenAll(
            RunAsync(_jobs.Reader, ProcessAsync, stoppingToken),
            RunAsync(_reads.Reader, ReadAsync, stoppingToken, start: () => ocr.IsAvailableAsync(stoppingToken)));
    }

    /// <param name="start">Run before the first job, without holding up the other lane.</param>
    private async Task RunAsync<T>(
        ChannelReader<T> jobs, Func<T, CancellationToken, Task> process, CancellationToken stoppingToken, Func<Task>? start = null)
        where T : ScamJob
    {
        try
        {
            if (start is not null)
            {
                await start();
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                var job = await jobs.ReadAsync(stoppingToken);
                try
                {
                    await process(job, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to handle scam post {MessageId} in {ChannelId}.", job.Post.MessageId, job.Post.ChannelId);
                }
                _lastChecked.Mark();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; anything still queued is dropped.
        }
    }

    private async Task ProcessAsync(ScamJob job, CancellationToken cancellationToken)
    {
        switch (job)
        {
            case ScamFollowUp followUp:
                await DeleteFollowUpAsync(followUp);
                break;

            case ScamDetection detection:
                await responder.ActAsync(detection, ScamOrigin.Detected, alert: true, cancellationToken);
                break;

            case ScamCandidate candidate:
                await CheckCandidateAsync(candidate, cancellationToken);
                break;
        }
    }

    private async Task CheckCandidateAsync(ScamCandidate candidate, CancellationToken cancellationToken)
    {
        if (await TryDeleteAsFollowUpAsync(candidate))
        {
            return;
        }

        // Not hashed if there's nothing to compare with; acting or reading hashes them anyway, to learn them.
        var images = candidate.Content.Images;
        var hashes = images.Count > 0 && known.ImageCount > 0 ? await hasher.HashAsync(images, cancellationToken) : null;
        var signals = candidate.Signals with { HashMatches = hashes is null ? 0 : known.CountImageMatches(hashes) };
        var settings = options.CurrentValue.Scams;
        var verdict = ScamRules.Decide(signals, settings);

        if (verdict != ScamVerdict.Scam && ScamRules.ShouldRead(signals, settings) && ocr.MightBeAvailable)
        {
            // Fetched now: by the time OCR gets to the post, it may be gone.
            hashes ??= await hasher.HashAsync(images, cancellationToken);
            var evidence = await responder.CaptureEvidenceAsync(images, cancellationToken);
            if (_reads.Reader.Count < ReadCapacity && _reads.Writer.TryWrite(new ScamRead(candidate, signals, hashes, evidence)))
            {
                return;
            }
            logger.LogWarning("OCR queue is full; deciding message {MessageId} in {ChannelId} without reading it.",
                candidate.Post.MessageId, candidate.Post.ChannelId);
            await ConcludeAsync(candidate, signals, hashes, evidence, cancellationToken);
            return;
        }

        await ConcludeAsync(candidate, signals, hashes, evidence: null, cancellationToken);
    }

    private async Task ReadAsync(ScamRead read, CancellationToken cancellationToken)
    {
        var candidate = read.Candidate;
        if (await TryDeleteAsFollowUpAsync(candidate))
        {
            return;
        }

        var signals = read.Signals;
        if (await ocr.IsAvailableAsync(cancellationToken))
        {
            // No rule needs more than 3 groups.
            var scan = await ocr.ScanAsync(candidate.Content.Images, stopAtGroups: 3, cancellationToken);
            signals = signals with { Words = scan.Groups, Keywords = scan.Keywords };
        }
        await ConcludeAsync(candidate, signals, read.Hashes, read.Evidence, cancellationToken);
    }

    private async Task ConcludeAsync(
        ScamCandidate candidate, ScamSignals signals, IReadOnlyList<ulong>? hashes, ScamEvidence? evidence, CancellationToken cancellationToken)
    {
        var verdict = ScamRules.Decide(signals, options.CurrentValue.Scams);

        // The post itself was checked when it arrived; its link previews only count for what they add.
        if (verdict == ScamVerdict.None
            || (candidate.LinkPreview && signals.HashMatches == 0 && ScamKeywords.Count(signals.Words) < 2))
        {
            return;
        }

        var detection = new ScamDetection(candidate.AuthorId, candidate.Post, candidate.Post.MessageId, candidate.Timestamp,
            candidate.Content, candidate.Posts, signals, hashes, evidence);
        if (verdict == ScamVerdict.Suspicious)
        {
            await responder.AlertSuspiciousAsync(detection, cancellationToken);
            return;
        }

        // The author may have been flagged by a burst while this post waited in the queue.
        var eventId = tracker.Flag(candidate.AuthorId, detection.EventId);
        if (eventId == detection.EventId)
        {
            await responder.ActAsync(detection, ScamOrigin.Detected, alert: true, cancellationToken);
        }
        else
        {
            await DeleteFollowUpAsync(new ScamFollowUp(candidate.AuthorId, candidate.Post, eventId));
        }
    }

    /// <summary>A post queued shortly before its author was acted on is more of the same: deleted without checking it
    /// further. Posts from after are follow-ups already; older ones were probably posted before the account was taken
    /// over, so they're checked as usual.</summary>
    private async Task<bool> TryDeleteAsFollowUpAsync(ScamCandidate candidate)
    {
        if (tracker.GetFlag(candidate.AuthorId) is not { } eventId
            || candidate.Timestamp < SnowflakeUtils.FromSnowflake(eventId) - TimeSpan.FromSeconds(options.CurrentValue.Scams.BurstWindowSeconds))
        {
            return false;
        }
        await DeleteFollowUpAsync(new ScamFollowUp(candidate.AuthorId, candidate.Post, eventId));
        return true;
    }

    private async Task DeleteFollowUpAsync(ScamFollowUp followUp)
    {
        var result = await moderator.DeleteFollowUpAsync(followUp.Post);
        stats.FollowUp(result == DeleteResult.Deleted);
        logger.LogInformation("Another post {MessageId} in {ChannelId} by flagged user {UserId} (event {EventId}){Action}.",
            followUp.Post.MessageId, followUp.Post.ChannelId, followUp.AuthorId, followUp.EventId, result switch
            {
                DeleteResult.Deleted => ", deleted",
                DeleteResult.AlreadyGone => ", already gone",
                DeleteResult.Failed => ", couldn't delete it",
                _ => "",
            });
    }

    private Task OnReadyAsync()
    {
        // Ready fires again after every reconnect.
        if (!_permissionsChecked)
        {
            _permissionsChecked = true;
            moderator.CheckPermissions();
        }
        return Task.CompletedTask;
    }
}
