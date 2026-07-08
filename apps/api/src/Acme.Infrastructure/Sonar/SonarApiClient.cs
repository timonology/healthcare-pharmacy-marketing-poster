using System.Net.Http.Json;
using Acme.Application.Sonar;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Sonar;

public sealed class SonarApiClient(
    HttpClient http,
    IOptions<SonarOptions> options,
    ILogger<SonarApiClient> logger) : ISonarApiClient
{
    private readonly SonarOptions _options = options.Value;

    public async Task<SonarPharmacyLookupResult?> LookupAsync(
        string pharmacyName,
        string address,
        CancellationToken ct)
    {
        if (_options.UseMock || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return MockLookup(pharmacyName);
        }

        try
        {
            var response = await http.PostAsJsonAsync(
                "/api/pharmacies/lookup",
                new { name = pharmacyName, address },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Sonar lookup failed: {Status} for {Pharmacy}",
                    response.StatusCode, pharmacyName);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<SonarApiResponse>(cancellationToken: ct);
            if (payload is null) return null;

            return new SonarPharmacyLookupResult(
                payload.FCode,
                payload.OdsCode,
                payload.RegulatoryStatus,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonar lookup threw for {Pharmacy}", pharmacyName);
            return null;
        }
    }

    private static SonarPharmacyLookupResult MockLookup(string pharmacyName)
    {
        var hash = Math.Abs(pharmacyName.GetHashCode());
        var fcode = $"F{hash % 99999:D5}";
        var ods = $"FA{hash % 999:D3}";
        return new SonarPharmacyLookupResult(fcode, ods, "Active", DateTime.UtcNow);
    }

    private sealed record SonarApiResponse(string? FCode, string? OdsCode, string? RegulatoryStatus);
}
