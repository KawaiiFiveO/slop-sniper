using Discord;

namespace SlopSniper.ScamImages;

public enum ScamVerdict
{
    None,

    /// <summary>Alert the moderators with buttons to act, but don't act.</summary>
    Suspicious,

    /// <summary>Delete, time out and alert.</summary>
    Scam,
}

/// <summary>When the bot acts, and on whom it never does.</summary>
public static class ScamRules
{
    /// <summary>A burst across enough channels, or two signals that back each other up. A single known image on its own
    /// only raises an alert, since a perceptual hash can mistake two similar-looking screenshots for each other.</summary>
    public static ScamVerdict Decide(ScamSignals signals, ScamImageOptions settings) => Decide(signals, settings, out _);

    /// <param name="reason">Which rule decided, for the debug command.</param>
    public static ScamVerdict Decide(ScamSignals signals, ScamImageOptions settings, out string reason)
    {
        var burst = signals.Channels >= 2;
        (var verdict, reason) =
            signals.Channels >= settings.BurstChannels ? (ScamVerdict.Scam, $"same images in {settings.BurstChannels}+ channels")
            : burst && signals.Shape ? (ScamVerdict.Scam, "same images in 2 channels, and scam shape")
            : signals.HashMatches >= 2 ? (ScamVerdict.Scam, "2+ images match known scam images")
            : signals.HashMatches >= 1 && burst ? (ScamVerdict.Scam, "known scam image, in 2 channels")
            : signals.HashMatches >= 1 && signals.Shape ? (ScamVerdict.Scam, "known scam image, and scam shape")
            : signals.HashMatches >= 1 ? (ScamVerdict.Suspicious, "1 known scam image with nothing backing it up")
            : (ScamVerdict.None, "no rule matched");
        return verdict;
    }

    /// <summary>The owner, members who can moderate, and exempt roles. Worked out from the member's role IDs, so it
    /// also works for members the bot hasn't cached.</summary>
    public static bool IsExempt(IGuild guild, IGuildUser member, ScamImageOptions settings)
    {
        if (member.Id == guild.OwnerId || member.IsBot || member.RoleIds.Any(settings.ExemptRoleIds.Contains))
        {
            return true;
        }

        // Every member has the @everyone role, whose ID is the guild's.
        var permissions = guild.Roles
            .Where(r => r.Id == guild.Id || member.RoleIds.Contains(r.Id))
            .Aggregate(0UL, (all, role) => all | role.Permissions.RawValue);
        var guildPermissions = new GuildPermissions(permissions);
        return guildPermissions.Administrator || guildPermissions.ManageMessages || guildPermissions.ModerateMembers;
    }
}
