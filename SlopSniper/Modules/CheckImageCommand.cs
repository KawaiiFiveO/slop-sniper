using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlopSniper.Options;
using SlopSniper.Services;

namespace SlopSniper.Modules;

/// <summary>Right-click a message > Apps > Check image provenance. The result is only shown to the moderator who
/// asked, and covers images that weren't detected or couldn't be checked.</summary>
public sealed class CheckImageCommand(ImageChecker checker, IOptionsMonitor<BotOptions> options, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    [DefaultMemberPermissions(GuildPermission.ManageMessages)]
    [CommandContextType(InteractionContextType.Guild)]
    [MessageCommand("Check image provenance")]
    public async Task CheckImage(IMessage message)
    {
        var images = ImageFormats.GetImages(message);
        if (images.Count == 0)
        {
            await RespondAsync("That message has no images.", ephemeral: true);
            return;
        }

        // Checks wait for the shared throttle, which can take longer than the 3 seconds Discord allows for a reply.
        await DeferAsync(ephemeral: true);

        List<AttachmentCheck> checks = [];
        foreach (var image in images)
        {
            checks.Add(await checker.CheckAsync(image, lifetime.ApplicationStopping));
        }

        var job = ScanJob.Create(message, Context.Guild.Id, Context.Channel.Id, images);
        await FollowupAsync(embed: ReportFormatter.BuildManualEmbed(job, checks, options.CurrentValue.MaxImageMiB), ephemeral: true);
    }
}
