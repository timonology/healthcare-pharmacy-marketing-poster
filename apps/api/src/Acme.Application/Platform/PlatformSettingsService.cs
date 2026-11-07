using Acme.Application.Admin;
using Acme.Application.Common;
using Acme.Domain.Admin;
using Acme.Domain.Platform;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Platform;

public interface IPlatformSettingRepository
{
    Task<PlatformSetting?> FindAsync(string key, CancellationToken ct);
    Task UpsertAsync(PlatformSetting setting, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// What the deployed environment supplies, before any override.
/// </summary>
/// <remarks>
/// A named abstraction rather than <c>IConfiguration</c> for two reasons: the
/// application layer should not have to know that "the default" happens to live
/// under a particular config key, and a service that reaches into
/// IConfiguration cannot be tested without building one.
/// </remarks>
public interface IPlatformDefaults
{
    /// <summary>Anthropic model from configuration, or the built-in default.</summary>
    string VideoModel { get; }

    /// <summary>True when an environment value was actually supplied.</summary>
    bool VideoModelWasConfigured { get; }
}

public sealed record VideoModelOptionDto(
    string Id,
    string DisplayName,
    string Description,
    decimal RelativeCost,
    bool IsCurrent);

/// <param name="Source">
/// Where the effective value came from: "database", "configuration", or
/// "built-in default". Shown in the UI because "why is it using that model?"
/// is otherwise a question only a developer can answer.
/// </param>
public sealed record VideoModelSettingDto(
    string CurrentModel,
    string Source,
    string? UpdatedByEmail,
    DateTime? UpdatedAtUtc,
    string ConfiguredFallback,
    IReadOnlyList<VideoModelOptionDto> Options);

public sealed record UpdateVideoModelRequest(string Model);

/// <summary>
/// Platform settings a SuperAdmin can change at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The resolution order is database → configuration → built-in default, and it
/// matters in both directions. A value set here overrides the environment, so
/// an operator can react without a deploy; but clearing it falls back to the
/// environment rather than to nothing, so a fresh install behaves exactly as it
/// did before this feature existed.
/// </para>
/// <para>
/// Only a SuperAdmin may write. That is enforced here as well as by the
/// controller policy — the policy answers "may this person enter the admin
/// area", and this answers "may this person change something that affects
/// every pharmacy at once".
/// </para>
/// </remarks>
public sealed class PlatformSettingsService(
    IPlatformSettingRepository settings,
    IAdminAuditRepository audit,
    IPlatformDefaults defaults,
    ILogger<PlatformSettingsService> log)
{
    private string ConfiguredModel => defaults.VideoModel;

    /// <summary>
    /// The model video generation should use right now.
    /// </summary>
    /// <remarks>
    /// Falls back rather than throwing if the stored value is no longer in the
    /// allow-list — a model can be retired by Anthropic while a row still
    /// points at it, and failing every video is a worse answer than quietly
    /// using the configured default and saying so in the log.
    /// </remarks>
    public async Task<string> GetVideoModelAsync(CancellationToken ct)
    {
        var stored = await settings.FindAsync(PlatformSettingKeys.VideoAnthropicModel, ct);

        if (stored is not null && VideoModels.IsAllowed(stored.Value))
            return stored.Value;

        if (stored is not null)
            log.LogWarning(
                "Stored video model '{Stored}' is not in the allowed list; falling back to {Fallback}",
                stored.Value, ConfiguredModel);

        return ConfiguredModel;
    }

    public async Task<Result<VideoModelSettingDto>> GetVideoModelSettingAsync(CancellationToken ct)
    {
        var stored = await settings.FindAsync(PlatformSettingKeys.VideoAnthropicModel, ct);
        var usable = stored is not null && VideoModels.IsAllowed(stored.Value);

        var current = usable ? stored!.Value : ConfiguredModel;

        var source = usable
            ? "database"
            : defaults.VideoModelWasConfigured
                ? "configuration"
                : "built-in default";

        var options = VideoModels.Allowed
            .Select(m => new VideoModelOptionDto(
                m.Id, m.DisplayName, m.Description, m.RelativeCost,
                IsCurrent: m.Id == current))
            .ToList();

        return Result<VideoModelSettingDto>.Success(new VideoModelSettingDto(
            current,
            source,
            usable ? stored!.UpdatedByEmail : null,
            usable ? stored!.UpdatedAtUtc : null,
            ConfiguredModel,
            options));
    }

    /// <summary>Sets the video model. SuperAdmin only.</summary>
    public async Task<Result<VideoModelSettingDto>> SetVideoModelAsync(
        AdminContext actor, string? model, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<VideoModelSettingDto>.Forbidden(
                "Only a SuperAdmin can change the video model.");

        if (string.IsNullOrWhiteSpace(model))
            return Result<VideoModelSettingDto>.Invalid("Choose a model.");

        // Rejected here rather than passed through. An unrecognised model would
        // reach the Anthropic API and fail on every video for every pharmacy,
        // with an error nobody would trace back to this screen.
        if (!VideoModels.IsAllowed(model))
            return Result<VideoModelSettingDto>.Invalid(
                $"'{model}' is not an allowed model. Choose one of: " +
                string.Join(", ", VideoModels.Allowed.Select(m => m.Id)));

        var trimmed = model.Trim();
        var existing = await settings.FindAsync(PlatformSettingKeys.VideoAnthropicModel, ct);
        var previous = existing?.Value ?? ConfiguredModel;

        if (existing is null)
        {
            await settings.UpsertAsync(
                PlatformSetting.Create(PlatformSettingKeys.VideoAnthropicModel, trimmed, actor.Email), ct);
        }
        else
        {
            existing.Update(trimmed, actor.Email);
            await settings.UpsertAsync(existing, ct);
        }

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId,
                actor.Email,
                AdminAction.ChangePlatformSetting,
                targetUserId: null,
                targetEmail: null,
                detail: $"Video model: {previous} → {trimmed}"),
            ct);

        log.LogInformation(
            "SuperAdmin {Actor} changed the video model from {Previous} to {New}",
            PersonalData.Mask(actor.Email), previous, trimmed);

        return await GetVideoModelSettingAsync(ct);
    }

    /// <summary>
    /// Clears the override so the configured value applies again.
    /// </summary>
    /// <remarks>
    /// Worth having as its own action rather than making someone guess which
    /// option matches their environment variable — after a few months nobody
    /// remembers what the deploy sets.
    /// </remarks>
    public async Task<Result<VideoModelSettingDto>> ResetVideoModelAsync(
        AdminContext actor, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<VideoModelSettingDto>.Forbidden(
                "Only a SuperAdmin can change the video model.");

        var existing = await settings.FindAsync(PlatformSettingKeys.VideoAnthropicModel, ct);
        if (existing is not null)
        {
            await settings.DeleteAsync(PlatformSettingKeys.VideoAnthropicModel, ct);

            await audit.AddAsync(
                AdminAuditEntry.Record(
                    actor.UserId, actor.Email, AdminAction.ChangePlatformSetting,
                    null, null, $"Video model reset to configured default ({ConfiguredModel})"),
                ct);

            log.LogInformation(
                "SuperAdmin {Actor} reset the video model to the configured default {Model}",
                PersonalData.Mask(actor.Email), ConfiguredModel);
        }

        return await GetVideoModelSettingAsync(ct);
    }
}
