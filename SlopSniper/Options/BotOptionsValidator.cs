using Microsoft.Extensions.Options;

namespace SlopSniper.Options;

public sealed class BotOptionsValidator : IValidateOptions<BotOptions>
{
    // The API rejects files over 50 MiB.
    private const int ApiMaxImageMiB = 50;

    private const int MaxQueueCapacity = 1000;

    public ValidateOptionsResult Validate(string? name, BotOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            errors.Add("Token is required.");
        }

        if (options.GuildId == 0)
        {
            errors.Add("GuildId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.OpenAiApiKey))
        {
            errors.Add("OpenAiApiKey is required.");
        }

        // Service alerts always go to the log channel, even when detections are only reported publicly.
        if (options.LogChannelId == 0)
        {
            errors.Add("LogChannelId is required.");
        }

        if (!Enum.IsDefined(options.ReportMode))
        {
            errors.Add("ReportMode must be Private, Public or Both.");
        }

        if (options.MaxImageMiB is < 1 or > ApiMaxImageMiB)
        {
            errors.Add($"MaxImageMiB must be between 1 and {ApiMaxImageMiB}.");
        }

        if (options.RequestIntervalSeconds < 1)
        {
            errors.Add("RequestIntervalSeconds must be at least 1.");
        }

        if (options.QueueCapacity is < 1 or > MaxQueueCapacity)
        {
            errors.Add($"QueueCapacity must be between 1 and {MaxQueueCapacity}.");
        }

        if (options.CacheHours < 0)
        {
            errors.Add("CacheHours can't be negative.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
