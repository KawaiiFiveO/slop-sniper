using Microsoft.Extensions.Options;

namespace SlopSniper.Options;

public sealed class BotOptionsValidator : IValidateOptions<BotOptions>
{
    public ValidateOptionsResult Validate(string? name, BotOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            errors.Add("Token is required.");
        }

        // Service alerts always go to the log channel, even when detections are only reported publicly.
        if (options.GuildId != 0 && options.LogChannelId == 0)
        {
            errors.Add("LogChannelId is required when GuildId is set.");
        }

        if (!options.Provenance.Enabled && !options.ScamImages.Enabled)
        {
            errors.Add("No module is enabled. Set Provenance.Enabled or ScamImages.Enabled to true.");
        }

        options.Provenance.Validate(errors);
        options.ScamImages.Validate(errors, options.GuildId);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
