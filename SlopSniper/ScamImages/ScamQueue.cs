using System.Threading.Channels;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.ScamImages;

/// <summary>Works through scam jobs one at a time: compares posts with known scam images, and carries out
/// detections. Separate from the provenance queue, so it never waits behind OpenAI's throttle.</summary>
public sealed class ScamQueue(
    DiscordSocketClient client,
    BurstTracker tracker,
    ScamHashStore known,
    ScamImageHasher hasher,
    ScamResponder responder,
    ScamModerator moderator,
    ScamStats stats,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamQueue> logger) : BackgroundService
{
    private const int Capacity = 100;

    private readonly Channel<ScamJob> _jobs = Channel.CreateUnbounded<ScamJob>();
    private bool _permissionsChecked;

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
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var job = await _jobs.Reader.ReadAsync(stoppingToken);
                try
                {
                    await ProcessAsync(job, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to handle scam post {MessageId} in {ChannelId}.", job.Post.MessageId, job.Post.ChannelId);
                }
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
                await responder.ActAsync(detection, ScamImageOrigin.Detected, alert: true, cancellationToken);
                break;

            case ScamCandidate candidate:
                await CheckCandidateAsync(candidate, cancellationToken);
                break;
        }
    }

    private async Task CheckCandidateAsync(ScamCandidate candidate, CancellationToken cancellationToken)
    {
        var hashes = await hasher.HashAsync(candidate.Images, cancellationToken);
        var signals = candidate.Signals with { HashMatches = known.CountMatches(hashes) };
        var verdict = ScamRules.Decide(signals, options.CurrentValue.ScamImages);
        if (verdict == ScamVerdict.None)
        {
            return;
        }

        var detection = new ScamDetection(candidate.AuthorId, candidate.Post, candidate.Post.MessageId, candidate.Timestamp,
            candidate.Images, candidate.Posts, signals, hashes);
        if (verdict == ScamVerdict.Suspicious)
        {
            await responder.AlertSuspiciousAsync(detection, cancellationToken);
            return;
        }

        // The author may have been flagged by a burst while this post waited in the queue.
        var eventId = tracker.Flag(candidate.AuthorId, detection.EventId);
        if (eventId == detection.EventId)
        {
            await responder.ActAsync(detection, ScamImageOrigin.Detected, alert: true, cancellationToken);
        }
        else
        {
            await DeleteFollowUpAsync(new ScamFollowUp(candidate.AuthorId, candidate.Post, eventId));
        }
    }

    private async Task DeleteFollowUpAsync(ScamFollowUp followUp)
    {
        var deleted = await moderator.DeleteFollowUpAsync(followUp.Post);
        stats.FollowUp(deleted);
        logger.LogInformation("Another image post {MessageId} in {ChannelId} by flagged user {UserId} (event {EventId}){Action}.",
            followUp.Post.MessageId, followUp.Post.ChannelId, followUp.AuthorId, followUp.EventId, deleted ? ", deleted" : "");
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
