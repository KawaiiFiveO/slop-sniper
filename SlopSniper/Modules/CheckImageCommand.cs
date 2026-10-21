using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Modules;

/// <summary>Right-click a message > Apps > Check image provenance. Registered globally, so it also works as a user
/// app in other servers and DMs. The result is only shown to whoever asked, and covers images that weren't detected
/// or couldn't be checked.</summary>
public sealed class CheckImageCommand(ImageChecker checker, IOptionsMonitor<BotOptions> options, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    // Discord's attachment limit. Attachments come first, so link previews never crowd them out.
    private const int MaxImagesPerCheck = 10;

    [IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
    [CommandContextType(InteractionContextType.Guild, InteractionContextType.BotDm, InteractionContextType.PrivateChannel)]
    [RequireCheckAccess]
    [MessageCommand("Check image provenance")]
    public async Task CheckImage(IMessage message)
    {
        var images = ImageFormats.GetImages(message, includeLinkPreviews: true);
        if (images.Count == 0)
        {
            await RespondAsync("That message has no images.", ephemeral: true);
            return;
        }

        // Checks wait for the shared throttle, which can take longer than the 3 seconds Discord allows for a reply.
        await DeferAsync(ephemeral: true);

        List<ImageCheck> checks = [];
        foreach (var image in images.Take(MaxImagesPerCheck))
        {
            checks.Add(await checker.CheckAsync(image, lifetime.ApplicationStopping));
        }

        // The bot may not be in this server or DM, so the IDs come from the interaction.
        var job = ScanJob.Create(message, Context.Interaction.GuildId, Context.Interaction.ChannelId ?? 0, images);
        var embed = ReportFormatter.BuildManualEmbed(job, checks, options.CurrentValue.MaxImageMiB, images.Count - checks.Count);
        await FollowupAsync(embed: embed, ephemeral: true);
    }
}
