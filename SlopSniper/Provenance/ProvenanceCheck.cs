namespace SlopSniper.Provenance;

/// <summary>A response from POST /v1/content_provenance_checks. Checks that don't apply to a file are left out of
/// <see cref="Results"/>.</summary>
public sealed record ProvenanceCheck(IReadOnlyList<ProvenanceSignal> Results)
{
    public bool Detected => Results.Any(r => r.Detected);
}

/// <param name="Type"><c>c2pa</c> or <c>synthid</c>.</param>
/// <param name="Outcome"><c>detected</c> or <c>not_detected</c>.</param>
/// <param name="ValidationState">C2PA only: <c>trusted</c>, <c>valid</c>, <c>invalid</c> or <c>not_present</c>.</param>
public sealed record ProvenanceSignal(
    string Type,
    string Outcome,
    string? ValidationState = null,
    string? Issuer = null,
    string? Model = null,
    string? GeneratedAt = null)
{
    public bool Detected => Outcome == "detected";
}
