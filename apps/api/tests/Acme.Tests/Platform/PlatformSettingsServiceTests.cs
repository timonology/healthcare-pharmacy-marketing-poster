using Acme.Application.Admin;
using Acme.Application.Common;
using Acme.Application.Platform;
using Acme.Domain.Admin;
using Acme.Domain.Platform;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Platform;

/// <summary>
/// The SuperAdmin gate, the fallback chain, and the audit trail.
/// </summary>
/// <remarks>
/// This setting changes what every video on the platform costs, so the two
/// things worth proving are that an ordinary Admin cannot touch it and that a
/// bad value cannot be stored.
/// </remarks>
public class PlatformSettingsServiceTests
{
    private sealed class FakeSettings : IPlatformSettingRepository
    {
        private readonly Dictionary<string, PlatformSetting> _rows = new();

        public int Writes { get; private set; }
        public int Deletes { get; private set; }

        public Task<PlatformSetting?> FindAsync(string key, CancellationToken ct) =>
            Task.FromResult(_rows.TryGetValue(key, out var v) ? v : null);

        public Task UpsertAsync(PlatformSetting setting, CancellationToken ct)
        {
            _rows[setting.Key] = setting; Writes++; return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _rows.Remove(key); Deletes++; return Task.CompletedTask;
        }

        /// <summary>Plants a value directly, bypassing validation — simulates a bad row.</summary>
        public void Plant(string key, string value) =>
            _rows[key] = PlatformSetting.Create(key, value, "seed@x.test");
    }

    private sealed class FakeAudit : IAdminAuditRepository
    {
        public List<AdminAuditEntry> Entries { get; } = new();

        public Task AddAsync(AdminAuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry); return Task.CompletedTask;
        }

        public Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct) =>
            Task.FromResult(new AuditPage(Entries, Entries.Count));
    }

    private sealed class FakeDefaults(string? configured) : IPlatformDefaults
    {
        public string VideoModel => configured ?? VideoModels.Fallback;
        public bool VideoModelWasConfigured => configured is not null;
    }

    private static readonly AdminContext SuperAdmin =
        new("su-1", "boss@x.test", UserRole.SuperAdmin);

    private static readonly AdminContext OrdinaryAdmin =
        new("ad-1", "admin@x.test", UserRole.Admin);

    private static (PlatformSettingsService Service, FakeSettings Repo, FakeAudit Audit) Build(
        string? configured = null)
    {
        var repo = new FakeSettings();
        var audit = new FakeAudit();
        var service = new PlatformSettingsService(
            repo, audit, new FakeDefaults(configured),
            NullLogger<PlatformSettingsService>.Instance);
        return (service, repo, audit);
    }

    /* ---------------- the gate ---------------- */

    [Fact]
    public async Task An_ordinary_admin_cannot_change_the_model()
    {
        var (service, repo, _) = Build();

        var result = await service.SetVideoModelAsync(OrdinaryAdmin, "claude-sonnet-5", default);

        Assert.Equal(ResultKind.Forbidden, result.Kind);
        // Refused *before* writing. A check that returns Forbidden after
        // persisting would be worse than no check at all.
        Assert.Equal(0, repo.Writes);
    }

    [Fact]
    public async Task An_ordinary_admin_cannot_reset_the_model()
    {
        var (service, repo, _) = Build();
        await service.SetVideoModelAsync(SuperAdmin, "claude-sonnet-5", default);

        var result = await service.ResetVideoModelAsync(OrdinaryAdmin, default);

        Assert.Equal(ResultKind.Forbidden, result.Kind);
        Assert.Equal(0, repo.Deletes);
    }

    [Fact]
    public async Task A_superadmin_can_change_the_model()
    {
        var (service, _, _) = Build();

        var result = await service.SetVideoModelAsync(SuperAdmin, "claude-sonnet-5", default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal("claude-sonnet-5", result.Value!.CurrentModel);
        Assert.Equal("database", result.Value.Source);
        Assert.Equal("boss@x.test", result.Value.UpdatedByEmail);
    }

    /* ---------------- validation ---------------- */

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("claude-haiku")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_model_outside_the_allow_list_is_refused(string model)
    {
        var (service, repo, _) = Build();

        var result = await service.SetVideoModelAsync(SuperAdmin, model, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Equal(0, repo.Writes);
    }

    [Fact]
    public async Task The_refusal_names_the_valid_options()
    {
        var (service, _, _) = Build();

        var result = await service.SetVideoModelAsync(SuperAdmin, "gpt-4o", default);

        // "Invalid model" alone leaves someone guessing. The message should be
        // enough to fix it without opening the code.
        Assert.Contains("claude-sonnet-5", result.Error);
    }

    /* ---------------- the fallback chain ---------------- */

    [Fact]
    public async Task With_nothing_stored_the_configured_value_wins()
    {
        var (service, _, _) = Build(configured: "claude-sonnet-5");

        Assert.Equal("claude-sonnet-5", await service.GetVideoModelAsync(default));

        var view = await service.GetVideoModelSettingAsync(default);
        Assert.Equal("configuration", view.Value!.Source);
    }

    [Fact]
    public async Task With_nothing_stored_or_configured_the_built_in_default_wins()
    {
        var (service, _, _) = Build(configured: null);

        Assert.Equal(VideoModels.Fallback, await service.GetVideoModelAsync(default));
        Assert.Equal("built-in default", (await service.GetVideoModelSettingAsync(default)).Value!.Source);
    }

    [Fact]
    public async Task A_stored_value_overrides_configuration()
    {
        var (service, _, _) = Build(configured: "claude-haiku-4-5-20251001");
        await service.SetVideoModelAsync(SuperAdmin, "claude-opus-5", default);

        Assert.Equal("claude-opus-5", await service.GetVideoModelAsync(default));
    }

    [Fact]
    public async Task A_stored_value_that_is_no_longer_allowed_falls_back_instead_of_failing()
    {
        // Anthropic retires a model while a row still points at it. Failing
        // every video would be a far worse answer than quietly using the
        // configured default and saying so in the log.
        var (service, repo, _) = Build(configured: "claude-sonnet-5");
        repo.Plant(PlatformSettingKeys.VideoAnthropicModel, "claude-retired-9");

        Assert.Equal("claude-sonnet-5", await service.GetVideoModelAsync(default));

        var view = await service.GetVideoModelSettingAsync(default);
        Assert.Equal("claude-sonnet-5", view.Value!.CurrentModel);
        Assert.Equal("configuration", view.Value.Source);
    }

    [Fact]
    public async Task Resetting_returns_to_the_configured_value()
    {
        var (service, repo, _) = Build(configured: "claude-haiku-4-5-20251001");
        await service.SetVideoModelAsync(SuperAdmin, "claude-opus-5", default);

        var result = await service.ResetVideoModelAsync(SuperAdmin, default);

        Assert.Equal("claude-haiku-4-5-20251001", result.Value!.CurrentModel);
        Assert.Equal("configuration", result.Value.Source);
        Assert.Equal(1, repo.Deletes);
    }

    [Fact]
    public async Task Resetting_when_nothing_is_stored_is_harmless()
    {
        var (service, repo, audit) = Build(configured: "claude-sonnet-5");

        var result = await service.ResetVideoModelAsync(SuperAdmin, default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal(0, repo.Deletes);
        // Nothing changed, so nothing is logged — an audit trail full of
        // no-ops buries the entries that record real change.
        Assert.Empty(audit.Entries);
    }

    /* ---------------- audit ---------------- */

    [Fact]
    public async Task Changing_the_model_is_recorded_with_both_values()
    {
        var (service, _, audit) = Build(configured: "claude-haiku-4-5-20251001");

        await service.SetVideoModelAsync(SuperAdmin, "claude-sonnet-5", default);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AdminAction.ChangePlatformSetting, entry.Action);
        Assert.Equal("boss@x.test", entry.ActorEmail);
        // Both sides of the change, so the log answers "what was it before?".
        Assert.Contains("claude-haiku-4-5-20251001", entry.Detail);
        Assert.Contains("claude-sonnet-5", entry.Detail);
        // No target user: the subject is every pharmacy at once.
        Assert.Null(entry.TargetUserId);
    }

    [Fact]
    public async Task A_refused_change_is_not_recorded()
    {
        var (service, _, audit) = Build();

        await service.SetVideoModelAsync(OrdinaryAdmin, "claude-sonnet-5", default);
        await service.SetVideoModelAsync(SuperAdmin, "gpt-4o", default);

        Assert.Empty(audit.Entries);
    }

    /* ---------------- the view ---------------- */

    [Fact]
    public async Task Exactly_one_option_is_marked_current()
    {
        var (service, _, _) = Build();
        await service.SetVideoModelAsync(SuperAdmin, "claude-sonnet-5", default);

        var view = await service.GetVideoModelSettingAsync(default);

        Assert.Single(view.Value!.Options, o => o.IsCurrent);
        Assert.Equal("claude-sonnet-5", view.Value.Options.Single(o => o.IsCurrent).Id);
    }
}
