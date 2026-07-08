namespace Acme.Application.Sonar;

public sealed record SonarPharmacyLookupResult(
    string? FCode,
    string? OdsCode,
    string? RegulatoryStatus,
    DateTime LookedUpAtUtc);

public interface ISonarApiClient
{
    /// <summary>
    /// Looks up extra pharmacy metadata from the Sonar Backend by name/address.
    /// Returns null when the upstream service can't find a match or is unreachable.
    /// </summary>
    Task<SonarPharmacyLookupResult?> LookupAsync(
        string pharmacyName,
        string address,
        CancellationToken ct);
}
