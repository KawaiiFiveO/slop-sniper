using Discord;
using Discord.Interactions;

namespace SlopSniper.Modules;

/// <summary>Discord.Net's RequireUserPermission, with a readable error instead of "User requires guild permission
/// ManageGuild."</summary>
public sealed class RequirePermissionAttribute(GuildPermission permission) : RequireUserPermissionAttribute(permission)
{
    // The inherited GuildPermission property hides the enum's name inside this class.
    public override string ErrorMessage => GuildPermission switch
    {
        Discord.GuildPermission.ManageGuild => "You need the Manage Server permission to do this.",
        Discord.GuildPermission.ManageMessages => "You need the Manage Messages permission to do this.",
        Discord.GuildPermission.ModerateMembers => "You need the Moderate Members permission to do this.",
        var other => $"You need the {other} permission to do this.",
    };
}
