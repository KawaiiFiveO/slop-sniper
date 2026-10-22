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

namespace SlopSniper.ScamImages;

/// <summary>Right-click a message > Apps > Debug scam check. Runs the scam checks on any message and shows how they
/// add up, for testing the rules. Never acts and never learns anything. Owner only, and only when the app is
/// installed on the owner's account.</summary>
public sealed class ScamDebugCommand(
    DiscordSocketClient client,
    ScamImageHasher hasher,
    ScamHashStore known,
    BurstTracker tracker,
    ScamModerator moderator,
    IOptions<BotOptions> startupOptions,
    IOptionsMonitor<BotOptions> options,
    IHostApplicationLifetime lifetime) : InteractionModuleBase<SocketInteractionContext>
{
    private static readonly Color ScamColor = new(0xE74C3C);
    private static readonly Color SuspiciousColor = new(0xE67E22);
    private static readonly Color NoneColor = new(0x95A5A6);

    [IntegrationType(ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.Guild, InteractionContextType.BotDm, InteractionContextType.PrivateChannel)]
    [RequireBotOwner]
    [MessageCommand("Debug scam check")]
    public async Task Debug(IMessage message)
    {
        // The same images the automatic scan sees: attachments, including forwarded ones, but not link previews.
        var images = ImageFormats.GetImages(message, includeLinkPreviews: false);
        if (images.Count == 0)
        {
            await RespondAsync("That message has no image attachments. (Link previews aren't scanned.)", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var settings = options.CurrentValue.ScamImages;
        var stopwatch = Stopwatch.StartNew();

        List<ImageHash> hashes = [];
        foreach (var image in images.Take(ScamImageHasher.MaxImages))
        {
            hashes.Add(await hasher.HashImageAsync(image, lifetime.ApplicationStopping));
        }
        var nearest = hashes.Select(h => h.Hash is { } hash ? known.FindNearest(hash) : null).ToList();
        var matches = nearest.Count(n => n?.Distance <= settings.HashDistance);

        // Burst and exemptions only mean something where the bot scans.
        var inHome = Context.Interaction.GuildId == startupOptions.Value.GuildId;
        var (channels, flagged) = inHome ? tracker.Inspect(message.Author.Id, images) : (0, false);
        var shape = ScamDetector.HasScamShape(images.Count, message.Content, settings);
        var signals = new ScamSignals(Math.Max(1, channels), shape, images.Count, matches);
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
            embed.AddField(Truncate(images[i].Name, 100), value);
        }

        var post = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Images: {images.Count} · text: {message.Content.Length} chars · scam shape: {(shape ? "yes" : "no")} ")
            .Append(CultureInfo.InvariantCulture, $"(needs {settings.ShapeMinImages}+ images and little text)\n");
        if (inHome)
        {
            post.Append(CultureInfo.InvariantCulture, $"Burst: {channels} channel(s) with these images in the last {settings.BurstWindowSeconds}s")
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
            ScamVerdict.Scam => "**Scam**: would delete, time out and learn the images",
            ScamVerdict.Suspicious => "**Possible scam**: would ask the moderators",
            _ => "**Nothing**",
        };
        embed.AddField("Verdict", $"{outcome}.\nRule: {reason}.");
        embed.WithFooter(string.Create(CultureInfo.InvariantCulture,
            $"{known.Count} known scam images · match within {settings.HashDistance} bits · took {stopwatch.ElapsedMilliseconds} ms"));

        await FollowupAsync(embed: embed.Build(), ephemeral: true);
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

    private async Task<string> DescribeAuthorAsync(IUser user, ScamImageOptions settings)
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
