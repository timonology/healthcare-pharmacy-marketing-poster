using Acme.Domain.Common;

namespace Acme.Domain.Platform;

/// <summary>
/// A platform-wide setting a SuperAdmin can change without a redeploy.
/// </summary>
/// <remarks>
/// <para>
/// Key/value rather than a column per setting, because the alternative is a
/// migration every time someone wants a new toggle. The tradeoff is that a
/// value is a string and nothing about the storage constrains it — so
/// validation lives in the service, and each key states its own rules.
/// </para>
/// <para>
/// Configuration remains the fallback. A setting that has never been changed
/// has no row at all, and the app uses whatever the environment supplies. That
/// ordering matters: it means deploying to a fresh environment behaves exactly
/// as it did before this feature existed.
/// </para>
/// </remarks>
public sealed class PlatformSetting : Entity
{
    /// <summary>Stable identifier, e.g. <c>video.anthropicModel</c>. Also the document id.</summary>
    public string Key { get; private set; } = default!;

    public string Value { get; private set; } = default!;

    /// <summary>Who last changed it. Shown in the UI so the answer is on screen, not in the audit log.</summary>
    public string UpdatedByEmail { get; private set; } = default!;

    private PlatformSetting() { }

    public static PlatformSetting Create(string key, string value, string updatedByEmail)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainException("A setting key is required.");
        if (value is null)
            throw new DomainException("A setting value is required.");

        return new PlatformSetting
        {
            Id = key.Trim(),
            Key = key.Trim(),
            Value = value.Trim(),
            UpdatedByEmail = updatedByEmail,
        };
    }

    public void Update(string value, string updatedByEmail)
    {
        if (value is null) throw new DomainException("A setting value is required.");
        Value = value.Trim();
        UpdatedByEmail = updatedByEmail;
        Touch();
    }
}

/// <summary>Setting keys, so the service and the repository cannot drift apart.</summary>
public static class PlatformSettingKeys
{
    /// <summary>Anthropic model used by the video generation pipeline.</summary>
    public const string VideoAnthropicModel = "video.anthropicModel";
}

/// <summary>
/// One Anthropic model a SuperAdmin may select for video generation.
/// </summary>
/// <param name="RelativeCost">
/// Cost per video relative to the cheapest option, for the UI. Approximate and
/// for orientation only — the true figure depends on script length.
/// </param>
public sealed record VideoModelOption(
    string Id,
    string DisplayName,
    string Description,
    decimal RelativeCost);

/// <summary>
/// The models that may be selected, and the default.
/// </summary>
/// <remarks>
/// <para>
/// <b>An allow-list, deliberately, rather than a free-text box.</b> This value is
/// passed straight to the Anthropic API by the Python worker. A typo would not
/// fail here — it would fail later, on every video, for every pharmacy, with an
/// error message about an unknown model that nobody would connect back to a
/// settings change made days earlier.
/// </para>
/// <para>
/// Adding a model is a one-line change here plus a deploy. That is the right
/// amount of friction for something that can silently break every video on the
/// platform.
/// </para>
/// </remarks>
public static class VideoModels
{
    /// <summary>
    /// Used when nothing is configured anywhere. Matches the historical
    /// hardcoded value so behaviour is unchanged on a fresh install.
    /// </summary>
    public const string Fallback = "claude-haiku-4-5-20251001";

    public static readonly IReadOnlyList<VideoModelOption> Allowed = new[]
    {
        new VideoModelOption(
            "claude-haiku-4-5-20251001",
            "Haiku 4.5",
            "Fastest and cheapest. Fine for short scripted narration, which is what a 15-second video needs.",
            1.0m),
        new VideoModelOption(
            "claude-sonnet-5",
            "Sonnet 5",
            "Noticeably better writing and research. Around 3x the cost per video.",
            3.0m),
        new VideoModelOption(
            "claude-opus-5",
            "Opus 5",
            "Strongest reasoning. Rarely worth it for 15 seconds of narration — the script is short and the model is the least of what makes it good.",
            5.0m),
    };

    public static bool IsAllowed(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && Allowed.Any(m => string.Equals(m.Id, model.Trim(), StringComparison.Ordinal));

    public static VideoModelOption? Find(string? model) =>
        model is null ? null : Allowed.FirstOrDefault(m => m.Id == model.Trim());
}
