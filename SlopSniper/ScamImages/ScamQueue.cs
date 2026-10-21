using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <summary>Carries out scam detections one at a time: saves evidence, deletes the copies, times out the author and
/// alerts the moderators. Separate from the provenance queue, so it never waits behind OpenAI's throttle.</summary>
public sealed class ScamQueue(
    DiscordSocketClient client,
    ScamModerator moderator,
    ImageDownloader downloader,
    Reporter reporter,
    ScamStats stats,
    IOptions<BotOptions> startupOptions,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamQueue> logger) : BackgroundService
{
    private const int Capacity = 100;

    // Big enough to recognise the scam in the alert, small enough to fetch quickly.
    private const int EvidenceMaxSide = 512;

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
                var deleted = await moderator.DeleteFollowUpAsync(followUp.Post);
                stats.FollowUp(deleted);
                logger.LogInformation("Another image post {MessageId} in {ChannelId} by flagged user {UserId} (event {EventId}){Action}.",
                    followUp.Post.MessageId, followUp.Post.ChannelId, followUp.AuthorId, followUp.EventId, deleted ? ", deleted" : "");
                break;

            case ScamDetection detection:
                await HandleDetectionAsync(detection, cancellationToken);
                break;
        }
    }

    private async Task HandleDetectionAsync(ScamDetection detection, CancellationToken cancellationToken)
    {
        // Fetched first: deleting the posts deletes their attachments too.
        var evidence = await GetEvidenceAsync(detection, cancellationToken);

        var actions = await moderator.ActAsync(detection.AuthorId, detection.Posts);
        stats.Record(actions);
        logger.LogInformation(
            "Scam images by {UserId} (event {EventId}) in {Channels} channels: deleted {Deleted} of {ToDelete} posts, timeout {Timeout}.",
            detection.AuthorId, detection.EventId, detection.Signals.Channels, actions.Deleted, actions.ToDelete,
            actions.TimedOutFor is not null ? "applied" : actions.TimeoutError ?? "off");

        var embed = ScamReportFormatter.BuildAlert(detection, actions, startupOptions.Value.GuildId,
            options.CurrentValue.ScamImages.BurstWindowSeconds, evidence?.FileName);
        var undo = ScamReportFormatter.BuildUndoButton(detection.EventId, detection.AuthorId);
        if (evidence is { } file)
        {
            using (file)
            {
                await reporter.SendToLogChannelAsync(embed: embed, components: undo, file: file);
            }
        }
        else
        {
            await reporter.SendToLogChannelAsync(embed: embed, components: undo);
        }
    }

    /// <summary>A small copy of the first unspoilered image, for the alert. The originals disappear with the posts.</summary>
    private async Task<FileAttachment?> GetEvidenceAsync(ScamDetection detection, CancellationToken cancellationToken)
    {
        if (detection.Images.FirstOrDefault(i => !i.IsSpoiler) is not { } image)
        {
            return null;
        }

        try
        {
            if (await downloader.DownloadResizedAsync(image, EvidenceMaxSide, cancellationToken) is { } bytes
                && ImageFormats.Sniff(bytes) is { } mediaType)
            {
                return new FileAttachment(new MemoryStream(bytes), "evidence." + mediaType["image/".Length..]);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't fetch evidence for {Image}: {Error}", image.LogName, ex.Message);
        }
        return null;
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
