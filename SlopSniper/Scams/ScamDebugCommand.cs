using System.Diagnostics;
using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlopSniper.Modules;
using SlopSniper.Options;
using SlopSniper.Provenance;
using SlopSniper.Services;

namespace SlopSniper.Scams;

/// <summary>Right-click a message > Apps > Debug scam check. Runs the scam checks on any message and shows how they
/// add up, for testing the rules. Never acts and never learns anything. Owner only, and only when the app is
/// installed on the owner's account.</summary>
public sealed class ScamDebugCommand(
    DiscordSocketClient client,
    ScamImageHasher hasher,
    OcrRunner ocr,
    KnownScams known,
    BurstTracker tracker,
    ScamModerator moderator,
    IOptions<BotOptions> startupOptions,
    IOptionsMonitor<BotOptions> options,
    IHostApplicationLifetime lifetime) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly Color ScamColor = new(0xE74C3C);
    private static readonly Color SuspiciousColor = new(0xE67E22);
    private static readonly Color NoneColor = new(0x95A5A6);

    // Enough OCR text per image to see what tesseract made of it.
    private const int MaxOcrText = 300;

    [IntegrationType(ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.Guild, InteractionContextType.BotDm, InteractionContextType.PrivateChannel)]
    [RequireBotOwner]
    [MessageCommand("Debug scam check")]
    public async Task Debug(IMessage message)
    {
        // What the automatic scan sees: attachments, including forwarded ones, and the links in the text. Link previews
        // are checked separately when Discord adds them, so they're shown when there are no attachments.
        var content = ScamContent.From(message);
        var fromPreviews = false;
        if (content.Images.Count == 0 && ImageFormats.GetImages(message, includeLinkPreviews: true) is { Count: > 0 } previews)
        {
            content = content with { Images = previews };
            fromPreviews = true;
        }
        if (content.Images.Count == 0 && content.Domains.Count == 0)
        {
            await RespondAsync("That message has no images or links, so the scan skips it.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var settings = options.CurrentValue.Scams;
        var stopwatch = Stopwatch.StartNew();
        var images = content.Images;

        List<ImageHash> hashes = [];
        foreach (var image in images.Take(ScamImageHasher.MaxImages))
        {
            hashes.Add(await hasher.HashImageAsync(image, lifetime.ApplicationStopping));
        }
        var nearest = hashes.Select(h => h.Hash is { } hash ? known.FindNearestImage(hash) : null).ToList();
        var matches = nearest.Count(n => n?.Distance <= settings.HashDistance);
        var domainMatches = known.CountDomainMatches(content.Domains);

        // Burst and exemptions only mean something where the bot scans.
        var inHome = Context.Interaction.GuildId == startupOptions.Value.GuildId;
        var linkText = content.Domains.Count > 0 ? content.Text : null;
        var (channels, repeated, flagged) = inHome ? tracker.Inspect(message.Author.Id, images, linkText) : (0, Repeated.None, false);
        var shape = !fromPreviews && ScamDetector.HasScamShape(images.Count, content.Text, settings);
        var signals = new ScamSignals(Math.Max(1, channels), repeated, shape, images.Count, matches, domainMatches);

        // Always read here, but the words only count where the automatic scan would read them.
        var wouldRead = ScamRules.ShouldRead(signals, settings) && ScamRules.Decide(signals, settings) != ScamVerdict.Scam;
        var scan = images.Count > 0 && await ocr.IsAvailableAsync(lifetime.ApplicationStopping)
            ? await ocr.ScanAsync(images, stopAtGroups: int.MaxValue, lifetime.ApplicationStopping)
            : OcrScan.Empty;
        if (wouldRead)
        {
            signals = signals with { Words = scan.Groups, Keywords = scan.Keywords };
        }
        var verdict = ScamRules.Decide(signals, settings, out var reason);
        var author = inHome ? await DescribeAuthorAsync(message.Author, settings) : null;

        var embed = new EmbedBuilder()
            .WithTitle("Scam check (debug)")
            .WithColor(verdict switch { ScamVerdict.Scam => ScamColor, ScamVerdict.Suspicious => SuspiciousColor, _ => NoneColor })
            .WithDescription("Nothing was done or learned.");

        for (var i = 0; i < images.Count; i++)
        {
            var value = i < hashes.Count
                ? DescribeImage(hashes[i], nearest[i], settings.HashDistance)
                : $"{DescribeFile(images[i])}\nNot hashed: only the first {ScamImageHasher.MaxImages} images of a post are.";
            if (i < scan.Images.Count)
            {
                value += "\n" + DescribeOcr(scan.Images[i]);
            }
            embed.AddField(Truncate(images[i].Name, 100), Truncate(value, 1024));
        }

        if (content.Domains.Count > 0)
        {
            embed.AddField("Links", Truncate(string.Join('\n', content.Domains.Select(d => DescribeDomain(d, settings))), 1024));
        }

        var post = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Images: {images.Count} · links: {content.Domains.Count} · text: {content.Text.Length} chars · ")
            .Append(CultureInfo.InvariantCulture, $"scam shape: {(shape ? "yes" : "no")} (needs {settings.ShapeMinImages}+ images and little text)\n");
        if (fromPreviews)
        {
            post.Append("The images are link previews, which are checked when Discord adds them.\n");
        }
        if (images.Count > 0)
        {
            post.Append(CultureInfo.InvariantCulture, $"OCR: {ocr.Status}. ")
                .Append(wouldRead ? "The scan would read this post.\n" : "The scan wouldn't read this post, so its words don't count here.\n");
        }
        if (inHome)
        {
            var what = repeated switch
            {
                Repeated.LinkText => "the same text and link",
                Repeated.Images | Repeated.LinkText => "the same images and text",
                Repeated.Images => "the same images",
                _ => linkText is null ? "these images" : "these images or this text and link",
            };
            post.Append(CultureInfo.InvariantCulture, $"Burst: {channels} channel(s) with {what} in the last {settings.BurstWindowSeconds}s")
                .Append(" (counts only posts the bot saw arrive)\n")
                .Append(CultureInfo.InvariantCulture, $"Author: {author}");
            if (flagged)
            {
                post.Append("\nAuthor is flagged from an earlier detection: this post would be deleted as a follow-up.");
            }
        }
        else
        {
            post.Append("Not in the scanned server, so burst and exemptions don't apply here.");
        }
        embed.AddField("Post", post.ToString());

        var outcome = verdict switch
        {
            ScamVerdict.Scam => "**Scam**: would delete, time out and learn the images and domains",
            ScamVerdict.Suspicious => "**Possible scam**: would ask the moderators",
            _ => "**Nothing**",
        };
        embed.AddField("Verdict", $"{outcome}.\nRule: {reason}.");
        embed.WithFooter(string.Create(CultureInfo.InvariantCulture,
            $"{known.ImageCount} known scam images, {known.DomainCount} domains · match within {settings.HashDistance} bits · took {stopwatch.ElapsedMilliseconds} ms"));

        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private string DescribeDomain(string domain, ScamOptions settings)
    {
        if (ScamLinks.IsTrusted(domain, settings.TrustedDomains))
        {
            return $"`{domain}`: trusted, never learned";
        }
        if (known.FindDomain(domain) is not { } match)
        {
            return $"`{domain}`: not a known scam domain";
        }

        var added = TimestampTag.FormatFromDateTimeOffset(match.AddedAt, TimestampTagStyles.Relative);
        var via = match.Domain == domain ? "" : $" via `{match.Domain}`";
        return $"`{domain}`: **known scam domain**{via} ({match.Origin}, event {match.EventId}, {added})";
    }

    private static string DescribeImage(ImageHash result, (KnownScamImage Known, int Distance)? nearest, int maxDistance)
    {
        var lines = new StringBuilder(DescribeFile(result.Image)).Append('\n');
        if (result.Hash is not { } hash)
        {
            return lines.Append(CultureInfo.InvariantCulture, $"No hash: {result.Error}.").ToString();
        }

        lines.Append(CultureInfo.InvariantCulture, $"Hash `{hash:x16}` ({result.Elapsed.TotalMilliseconds:0} ms)\n");
        if (nearest is not { } match)
        {
            return lines.Append("No known scam images to compare with.").ToString();
        }

        var added = TimestampTag.FormatFromDateTimeOffset(match.Known.AddedAt, TimestampTagStyles.Relative);
        return lines
            .Append(CultureInfo.InvariantCulture, $"Closest known image: {match.Distance} bits apart, ")
            .Append(match.Distance <= maxDistance ? "**a match**" : "not a match")
            .Append(CultureInfo.InvariantCulture, $" ({match.Known.Origin}, event {match.Known.EventId}, {added})")
            .ToString();
    }

    private static string DescribeOcr(OcrResult result)
    {
        if (result.Text is not { } text)
        {
            return $"OCR failed: {result.Error}.";
        }

        var words = result.Groups == ScamWords.None
            ? "no scam words"
            : $"{ScamKeywords.Describe(result.Groups)}: {string.Join(", ", result.Keywords)}";
        var shown = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace('`', '\'');
        return string.Create(CultureInfo.InvariantCulture, $"OCR ({result.Elapsed.TotalMilliseconds:0} ms): {words}")
            + (shown.Length > 0 ? $"\n```\n{Truncate(shown, MaxOcrText)}\n```" : "");
    }

    private static string DescribeFile(ImageSource image)
    {
        List<string> parts = [];
        if (image is { Width: { } width, Height: { } height })
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{width}×{height}"));
        }
        if (image.Size is { } size)
        {
            parts.Add(ImageChecker.FormatMiB(size));
        }
        if (image.ContentType is { } contentType)
        {
            parts.Add(contentType);
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : "size unknown";
    }

    private async Task<string> DescribeAuthorAsync(IUser user, ScamOptions settings)
    {
        if (user.IsWebhook)
        {
            return "a webhook, which the bot never acts on";
        }
        if (client.GetGuild(startupOptions.Value.GuildId) is not { } guild || await moderator.GetMemberAsync(user.Id) is not { } member)
        {
            return "no longer a member";
        }
        return ScamRules.IsExempt(guild, member, settings)
            ? "**exempt** (owner, bot, moderator or exempt role): the bot would skip this post entirely"
            : "not exempt";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
}
