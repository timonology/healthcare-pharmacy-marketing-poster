using Acme.Application.Platform;
using Acme.Domain.Platform;
using Microsoft.Extensions.Configuration;

namespace Acme.Infrastructure.Platform;

/// <summary>
/// Reads the environment's idea of the defaults.
/// </summary>
/// <remarks>
/// Both key spellings are checked because the Python worker reads the raw
/// <c>ANTHROPIC_MODEL</c> environment variable while .NET convention would
/// spell it <c>AiService:AnthropicModel</c>. If the two disagreed about what
/// "the configured default" is, the admin screen would report one model while
/// the worker used another — the sort of discrepancy that takes an afternoon to
/// spot.
/// </remarks>
public sealed class ConfigurationPlatformDefaults(IConfiguration configuration) : IPlatformDefaults
{
    private string? Raw =>
        Trimmed(configuration["AiService:AnthropicModel"])
        ?? Trimmed(configuration["ANTHROPIC_MODEL"]);

    public string VideoModel => Raw ?? VideoModels.Fallback;

    public bool VideoModelWasConfigured => Raw is not null;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
