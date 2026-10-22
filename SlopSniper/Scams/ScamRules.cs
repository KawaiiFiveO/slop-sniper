using Discord;

namespace SlopSniper.Scams;

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
    /// <summary>A burst across enough channels, or two signals that back each other up. A single known image or domain
    /// on its own only raises an alert, since a perceptual hash can mistake two similar-looking screenshots for each
    /// other, and a learned domain may have been an innocent link in a scam post.</summary>
    public static ScamVerdict Decide(ScamSignals signals, ScamOptions settings) => Decide(signals, settings, out _);

    /// <param name="reason">Which rule decided, for the debug command and the audit log.</param>
    public static ScamVerdict Decide(ScamSignals signals, ScamOptions settings, out string reason)
    {
        var burst = signals.Channels >= 2;
        var repeated = signals.Repeated == Repeated.LinkText ? "text and link" : "images";
        var known = signals.HashMatches + signals.DomainMatches;
        var knownOne = signals.HashMatches > 0 ? "known scam image" : "known scam domain";
        var knownTwo = signals.HashMatches >= 2 ? "2+ images match known scam images"
            : signals.DomainMatches >= 2 ? "links to 2+ known scam domains"
            : "known scam image and known scam domain";

        (var verdict, reason) =
            signals.Channels >= settings.BurstChannels ? (ScamVerdict.Scam, $"same {repeated} in {settings.BurstChannels}+ channels")
            : burst && signals.Shape ? (ScamVerdict.Scam, $"same {repeated} in 2 channels, and scam shape")
            : known >= 2 ? (ScamVerdict.Scam, knownTwo)
            : known >= 1 && burst ? (ScamVerdict.Scam, $"{knownOne}, in 2 channels")
            : known >= 1 && signals.Shape ? (ScamVerdict.Scam, $"{knownOne}, and scam shape")
            : known >= 1 ? (ScamVerdict.Suspicious, $"1 {knownOne} with nothing backing it up")
            : (ScamVerdict.None, "no rule matched");
        return verdict;
    }

    /// <summary>The owner, members who can moderate, and exempt roles. Worked out from the member's role IDs, so it
    /// also works for members the bot hasn't cached.</summary>
    public static bool IsExempt(IGuild guild, IGuildUser member, ScamOptions settings)
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
