using Discord;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.ScamImages;

/// <param name="Learned">New known scam images learned from the post.</param>
public sealed record ScamOutcome(ScamActions Actions, int Learned);

/// <summary>Carries out a detection: keeps evidence, learns the images, deletes, times out and alerts. Shared by
/// automatic detections, the alert buttons and "Report as scam".</summary>
public sealed class ScamResponder(
    ScamModerator scamModerator,
    ScamImageHasher hasher,
    ScamHashStore known,
    ImageDownloader downloader,
    Reporter reporter,
    ScamStats stats,
    IOptions<BotOptions> startupOptions,
    IOptionsMonitor<BotOptions> options,
    ILogger<ScamResponder> logger)
{
    // Big enough to recognise the scam in the alert, small enough to fetch quickly.
    private const int EvidenceMaxSide = 512;

    /// <param name="alert">False when the caller updates an existing alert instead of posting a new one.</param>
    /// <param name="moderator">Username of the moderator who reported or confirmed it, for the audit log.</param>
    public async Task<ScamOutcome> ActAsync(
        ScamDetection detection, ScamImageOrigin origin, bool alert, CancellationToken cancellationToken, string? moderator = null)
    {
        // Both fetched first: deleting the posts deletes their attachments too.
        var evidence = alert ? await GetEvidenceAsync(detection.Images, cancellationToken) : null;
        var hashes = detection.Hashes ?? await hasher.HashAsync(detection.Images, cancellationToken);
        var learned = await known.AddAsync(hashes, detection.EventId, origin);

        var actions = await scamModerator.ActAsync(detection.AuthorId, detection.Posts, GetAuditReason(detection.Signals, origin, moderator));
        var outcome = new ScamOutcome(actions, learned);
        stats.Record(actions);
        logger.LogInformation(
            "Scam images by {UserId} (event {EventId}, {Origin}): deleted {Deleted} of {ToDelete} posts, timeout {Timeout}, learned {Learned} images.",
            detection.AuthorId, detection.EventId, origin, actions.Deleted, actions.ToDelete,
            actions.TimedOutFor is not null ? "applied" : actions.TimeoutError ?? "off", learned);

        if (alert)
        {
            var embed = ScamReportFormatter.BuildAlert(detection, outcome, startupOptions.Value.GuildId,
                options.CurrentValue.ScamImages.BurstWindowSeconds, evidence?.FileName);
            await SendAsync(embed, ScamReportFormatter.BuildUndoButton(detection.EventId, detection.AuthorId), evidence);
        }
        return outcome;
    }

    /// <summary>Asks the moderators about a post that isn't certain enough to act on.</summary>
    public async Task AlertSuspiciousAsync(ScamDetection detection, CancellationToken cancellationToken)
    {
        stats.Suspicious();
        logger.LogInformation("Possible scam images by {UserId} in message {MessageId}: {Matches} known image matches.",
            detection.AuthorId, detection.Post.MessageId, detection.Signals.HashMatches);

        var evidence = await GetEvidenceAsync(detection.Images, cancellationToken);
        var embed = ScamReportFormatter.BuildSuspiciousAlert(detection, startupOptions.Value.GuildId,
            options.CurrentValue.ScamImages.BurstWindowSeconds, evidence?.FileName);
        await SendAsync(embed, ScamReportFormatter.BuildSuspiciousButtons(detection.Post, detection.AuthorId), evidence);
    }

    /// <summary>Why the posts were deleted and the author timed out, for Discord's audit log.</summary>
    private string GetAuditReason(ScamSignals signals, ScamImageOrigin origin, string? moderator)
    {
        ScamRules.Decide(signals, options.CurrentValue.ScamImages, out var rule);
        return "slop-sniper: " + origin switch
        {
            ScamImageOrigin.Reported => $"reported as scam by {moderator}",
            ScamImageOrigin.Confirmed => $"possible scam confirmed by {moderator}",
            _ => $"scam images ({rule})",
        };
    }

    private async Task SendAsync(Embed embed, MessageComponent components, FileAttachment? evidence)
    {
        if (evidence is { } file)
        {
            using (file)
            {
                await reporter.SendToLogChannelAsync(embed: embed, components: components, file: file);
            }
        }
        else
        {
            await reporter.SendToLogChannelAsync(embed: embed, components: components);
        }
    }

    /// <summary>A small copy of the first unspoilered image, for the alert. The originals disappear with the posts.</summary>
    private async Task<FileAttachment?> GetEvidenceAsync(IReadOnlyList<ImageSource> images, CancellationToken cancellationToken)
    {
        if (images.FirstOrDefault(i => !i.IsSpoiler) is not { } image)
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
}
