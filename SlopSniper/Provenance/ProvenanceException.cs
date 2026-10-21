namespace SlopSniper.Provenance;

public enum ProvenanceFailure
{
    /// <summary>429 rate_limit_exceeded. Retry after <see cref="ProvenanceException.RetryAfter"/>.</summary>
    RateLimited,

    /// <summary>429 insufficient_quota. Retrying soon won't help.</summary>
    QuotaExceeded,

    /// <summary>401, 403 or 404: a bad key, a missing permission, or no access to the endpoint.</summary>
    AccessDenied,

    /// <summary>The file was rejected (malformed, unsupported or blocked). Retrying won't help.</summary>
    BadFile,

    /// <summary>Network errors, timeouts and server errors. Worth retrying.</summary>
    Transient,
}

public sealed class ProvenanceException(
    ProvenanceFailure failure, string message, TimeSpan? retryAfter = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ProvenanceFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
