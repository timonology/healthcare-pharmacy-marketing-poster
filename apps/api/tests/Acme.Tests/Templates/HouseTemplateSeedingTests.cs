using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Application.Audit;
using Acme.Application.Templates;
using Acme.Domain.Templates;
using Acme.Infrastructure.Persistence;
using Acme.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Templates;

/// <summary>
/// The house-style set: what ships, and how it is seeded.
/// </summary>
/// <remarks>
/// The designs themselves — wrapping, contrast, house rules — are checked where
/// they are generated (<c>apps/ai-service/tests/test_house_templates.py</c>).
/// These check the API's side: the embedded set is the one asked for, and the
/// seeder never inserts a template whose photograph is not in storage, never
/// touches a template that already exists, and never fails startup.
/// </remarks>
public class HouseTemplateSeedingTests
{
    private static readonly (string Name, TemplateCategory Category)[] Expected =
    [
        ("Trusted circle", TemplateCategory.General),
        ("Diagonal care", TemplateCategory.General),
        ("Team wave", TemplateCategory.General),
        ("Advice checklist", TemplateCategory.General),
        ("Book with us", TemplateCategory.General),
        ("Local pharmacy", TemplateCategory.General),
        ("Know your numbers", TemplateCategory.Awareness),
        ("Diabetes risk check", TemplateCategory.Awareness),
        ("Flu vaccination", TemplateCategory.Vaccination),
        ("Winter vaccinations", TemplateCategory.Vaccination),
        ("Travel clinic", TemplateCategory.Vaccination),
        ("Weight management", TemplateCategory.Awareness),
        ("Shingles vaccination", TemplateCategory.Vaccination),
        ("Pneumonia vaccination", TemplateCategory.Vaccination),
        ("Stop smoking support", TemplateCategory.Awareness),
        ("Contraception advice", TemplateCategory.Awareness),
        ("Pharmacy First", TemplateCategory.Promotion),
        ("Prescription delivery", TemplateCategory.Promotion),
        ("Antibiotics awareness", TemplateCategory.Safety),
        ("Return unused medicines", TemplateCategory.Safety),
        ("Hay fever help", TemplateCategory.Seasonal),
        ("Repeat prescriptions", TemplateCategory.General),
    ];

    /// <summary>Designed without a photograph: their natural photos are branded packaging.</summary>
    private static readonly HashSet<string> PhotoFree =
        ["sys-house-antibiotics", "sys-house-medicine-returns"];

    private static readonly string[] Names = Expected.Select(e => e.Name).ToArray();

    private static List<Template> House() =>
        TemplateSeeder.DefaultTemplatesForTesting().Where(t => t.Id.StartsWith("sys-house-")).ToList();

    [Fact]
    public void The_house_templates_ship_under_the_names_and_categories_asked_for()
    {
        House().Select(t => (t.Name, t.Category)).Should().Equal(Expected);
    }

    [Fact]
    public void The_newer_set_is_dated_after_the_first_so_the_gallery_shows_it_first()
    {
        var first = House().Single(t => t.Name == "Trusted circle").CreatedAtUtc;
        var second = House().Single(t => t.Name == "Flu vaccination").CreatedAtUtc;

        second.Should().BeAfter(first);
        first.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Each_is_an_A4_canvas_with_one_booking_qr_and_the_three_tokens()
    {
        foreach (var t in House())
        {
            using var doc = JsonDocument.Parse(t.CanvasJson);
            doc.RootElement.GetProperty("width").GetInt32().Should().Be(1240, t.Name);
            doc.RootElement.GetProperty("height").GetInt32().Should().Be(1754, t.Name);

            var shapes = doc.RootElement.GetProperty("shapes").EnumerateArray().ToList();
            shapes.Count(s => s.GetProperty("id").GetString() == "pharmacy-booking-qr")
                .Should().Be(1, $"{t.Name} carries exactly one booking QR");

            foreach (var token in new[] { "{{pharmacy_name}}", "{{phone}}", "{{address}}" })
                Regex.Matches(t.CanvasJson, Regex.Escape(token)).Count
                    .Should().Be(1, $"{t.Name} shows {token} once");
        }
    }

    [Fact]
    public void Every_photo_a_canvas_uses_is_one_the_seeder_uploads()
    {
        var assets = TemplateSeeder.HouseAssetsForTesting();
        foreach (var t in House())
        {
            var referenced = Regex.Matches(t.CanvasJson, @"/api/assets/templates/([0-9a-f]{32}\.(?:jpg|png))")
                .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();

            if (PhotoFree.Contains(t.Id))
                referenced.Should().BeEmpty($"{t.Name} is photo-free by design");
            else
                referenced.Should().NotBeEmpty(t.Name);
            assets[t.Id].OrderBy(x => x).Should().Equal(referenced,
                $"{t.Name}: a photo the canvas uses but the seeder does not upload is a grey box");
            // A loop, not OnlyContain: FluentAssertions fails OnlyContain on an
            // empty collection, and a photo-free template legitimately has none.
            foreach (var f in referenced)
                TemplateAssetUrls.IsSafeFileName(f).Should().BeTrue($"{t.Name}: {f}");
        }
    }

    [Fact]
    public async Task A_template_already_in_the_database_is_never_rewritten()
    {
        // Insert-only: an admin's edit to a built-in template survives a deploy.
        var existing = TemplateSeeder.DefaultTemplatesForTesting().ToList();
        var repo = new FakeRepo(existing);
        var blobs = new FakeBlobs { AlreadyStored = true };

        await Seeder(repo, blobs).SeedIfEmptyAsync(CancellationToken.None);

        repo.Added.Should().BeEmpty();
        blobs.Uploaded.Should().BeEmpty();
    }

    [Fact]
    public async Task A_house_template_whose_photo_is_already_stored_is_inserted()
    {
        var repo = new FakeRepo([]);
        await Seeder(repo, new FakeBlobs { AlreadyStored = true }).SeedIfEmptyAsync(CancellationToken.None);

        repo.Added.Select(t => t.Name).Should().Contain(Names);
    }

    [Fact]
    public async Task Storage_being_down_holds_the_house_set_back_without_failing_startup()
    {
        var repo = new FakeRepo([]);
        var blobs = new FakeBlobs { Broken = true };
        var seed = () => Seeder(repo, blobs).SeedIfEmptyAsync(CancellationToken.None);

        await seed.Should().NotThrowAsync("an unreachable blob store must not stop the API starting");
        blobs.Calls.Should().Be(1,
            "after the first failure the rest wait for the next start — each call to an "
            + "unreachable store costs seconds of retries during startup");
        repo.Added.Should().NotContain(t => t.Id.StartsWith("sys-house-") && !PhotoFree.Contains(t.Id),
            "a template whose photo could not be stored would publish a grey box, "
            + "and insert-only seeding would never come back to fix it");
        repo.Added.Select(t => t.Id).Should().Contain(PhotoFree,
            "a photo-free design needs nothing from storage, so an outage must not hold it back");
        repo.Added.Should().Contain(t => t.Id == "sys-vaccination-clinic",
            "the other built-in templates are unaffected");
    }

    [Fact]
    public async Task A_missing_photo_is_uploaded_from_the_build_or_the_template_waits()
    {
        var repo = new FakeRepo([]);
        var blobs = new FakeBlobs { AlreadyStored = false };
        await Seeder(repo, blobs).SeedIfEmptyAsync(CancellationToken.None);

        foreach (var (id, files) in TemplateSeeder.HouseAssetsForTesting())
        {
            var built = files.All(f => TemplateSeeder.OpenEmbeddedAsset(f) is { } s && s.Length > 0);
            if (built)
            {
                // Uploaded under the key the asset URL resolves to, then inserted.
                // A loop for the same reason: a photo-free template has no files,
                // and "nothing to upload" is a pass, not a failure.
                foreach (var f in files)
                    blobs.Uploaded.Should().Contain($"templates/assets/{f}", id);
                repo.Added.Should().Contain(t => t.Id == id);
            }
            else
            {
                // Photos not built into this assembly yet: held back, not inserted.
                repo.Added.Should().NotContain(t => t.Id == id);
            }
        }
    }

    private static TemplateSeeder Seeder(FakeRepo repo, FakeBlobs blobs, FakeAudit? audit = null) =>
        new(repo, blobs, audit ?? new FakeAudit(), NullLogger<TemplateSeeder>.Instance);

    [Fact]
    public async Task Every_inserted_template_is_recorded_as_system_work_and_nothing_else_is()
    {
        var repo = new FakeRepo([]);
        var audit = new FakeAudit();
        await Seeder(repo, new FakeBlobs { AlreadyStored = true }, audit).SeedIfEmptyAsync(CancellationToken.None);

        audit.Recorded.Select(e => e.EntityId).Should().BeEquivalentTo(repo.Added.Select(t => t.Id),
            "one event per template actually inserted — none for those skipped");
        audit.Recorded.Should().OnlyContain(e =>
            e.ActorKind == ActorKinds.System && e.Action == "system.seed.template",
            "a seeder is a machine and must stay off the staff security view");
    }

    /// <summary>Keeps what the seeder records, so a test can read it back.</summary>
    private sealed class FakeAudit : IAuditLogger
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent e, CancellationToken ct)
        {
            Recorded.Add(e);
            return Task.CompletedTask;
        }

        public Task<bool> RecordActivityAsync(AuditEvent e, TimeSpan dedupeWindow, CancellationToken ct) =>
            throw new NotSupportedException("Seeding records changes, never views.");
    }

    private sealed class FakeRepo(IEnumerable<Template> existing) : ITemplateRepository
    {
        private readonly Dictionary<string, Template> _rows = existing.ToDictionary(t => t.Id);
        public List<Template> Added { get; } = [];

        public Task<Template?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(_rows.GetValueOrDefault(id));

        public Task AddAsync(Template template, CancellationToken ct)
        {
            Added.Add(template);
            _rows[template.Id] = template;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Template>> ListAsync(TemplateQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Template>>(_rows.Values.ToList());
        public Task<int> CountAsync(TemplateQuery query, CancellationToken ct) => Task.FromResult(_rows.Count);
        public Task UpdateAsync(Template template, CancellationToken ct) =>
            throw new InvalidOperationException("the seeder must never update a template");
        public Task DeleteAsync(string id, CancellationToken ct) =>
            throw new InvalidOperationException("the seeder must never delete a template");
        public Task<bool> AnyAsync(CancellationToken ct) => Task.FromResult(_rows.Count > 0);
    }

    private sealed class FakeBlobs : IBlobStorageService
    {
        public bool AlreadyStored { get; init; }
        public bool Broken { get; init; }
        public List<string> Uploaded { get; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct)
        {
            Calls++;
            if (Broken) throw new IOException("storage unreachable");
            IReadOnlyList<string> found = AlreadyStored || Uploaded.Contains(prefix)
                ? new[] { prefix }
                : Array.Empty<string>();
            return Task.FromResult(found);
        }

        public Task<string> UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct)
        {
            if (Broken) throw new IOException("storage unreachable");
            Uploaded.Add(blobKey);
            return Task.FromResult(blobKey);
        }

        public Task<Stream> DownloadAsync(string blobKey, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string blobKey, CancellationToken ct) => Task.CompletedTask;
        public Uri GetReadOnlyUri(string blobKey, TimeSpan validFor) => new("https://example.invalid/" + blobKey);
    }
}
