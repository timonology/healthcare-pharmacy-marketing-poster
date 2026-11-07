using System.Text.Json;
using Acme.Domain.Templates;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// The house-style set: six photographic templates in one pharmacy style.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated, not hand-written.</b> The canvases come from
/// <c>apps/ai-service/app/tools/house_templates.py</c>, which computes the wave
/// polygons and checks every line of text against both the editor's and the
/// PDF's wrapping before writing <c>HouseTemplates/templates.json</c>. The
/// photographs come from <c>house_template_assets.py</c>, which bakes the
/// circle crops and fades into the files. Both are embedded in this assembly,
/// so a fresh environment gets the set with no network and no Pexels key.
/// </para>
/// <para>
/// <b>Photographs go to template storage before a template is inserted.</b>
/// A canvas points at <c>/api/assets/templates/{guid}</c>, which is served from
/// blob storage — so the file must be there first. If it cannot be (storage
/// down, or the photo was never built into this assembly) the template is held
/// back and tried again on the next start. Inserting it anyway would publish a
/// poster with a grey box where the pharmacist should be, and insert-only
/// seeding would never come back to fix it.
/// </para>
/// </remarks>
public sealed partial class TemplateSeeder
{
    private const string ResourcePrefix = "HouseTemplates.";

    private bool _storageUnavailable;

    /// <summary>template id → the asset files its canvas references.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> AssetsByTemplate =
        new(() => LoadDefinitions().ToDictionary(d => d.Id, d => d.Assets));

    private static IEnumerable<Template> HouseTemplates() =>
        LoadDefinitions().Select(d => Template.CreateWithId(
            d.Id,
            d.Name,
            d.Description,
            d.Category,
            d.Tags,
            Canvas(d.Name, d.Background, d.ShapesJson),
            // Each batch has its own release date, so the gallery shows the
            // newest set first and the order survives restarts.
            createdAtUtc: d.ReleasedUtc));

    /// <summary>
    /// Puts a template's photographs into template storage. False when the
    /// template must wait: a photo missing from this build, or storage down.
    /// </summary>
    private async Task<bool> EnsureAssetsAsync(string templateId, CancellationToken ct)
    {
        if (!AssetsByTemplate.Value.TryGetValue(templateId, out var files))
            return true; // not a house template — nothing to upload
        if (files.Length == 0)
            return true; // a photo-free design needs nothing from storage

        // Once storage has failed, the rest wait for the next start. The Azure
        // client retries with backoff on every call, so asking again for each
        // template would hold up API startup by half a minute to learn nothing.
        if (_storageUnavailable) return false;

        foreach (var file in files)
        {
            var key = $"{TemplateAssetUrls.BlobPrefix}/{file}";
            try
            {
                // Listing by the full key returns it, or nothing.
                var present = (await _blobs.ListKeysAsync(key, ct))
                    .Contains(key, StringComparer.Ordinal);
                if (present) continue;

                await using var stream = OpenEmbeddedAsset(file);
                if (stream is null)
                {
                    _logger.LogWarning(
                        "Template {TemplateId} not seeded: its photo {File} is not in this build. "
                        + "Run app.tools.house_template_assets (see docs/TEMPLATE_AUTHORING.md) "
                        + "and rebuild the API.", templateId, file);
                    return false;
                }

                await _blobs.UploadAsync(key, stream, TemplateAssetUrls.ContentTypeFor(file), ct);
                _logger.LogInformation("Uploaded template photo {File} for {TemplateId}", file, templateId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _storageUnavailable = true;
                _logger.LogWarning(ex,
                    "Template {TemplateId} not seeded this time: could not store {File}. "
                    + "It will be tried again on the next start.", templateId, file);
                return false;
            }
        }
        return true;
    }

    /// <summary>A photo embedded in this assembly, or null if it was not built in.</summary>
    public static Stream? OpenEmbeddedAsset(string fileName) =>
        TemplateAssetUrls.IsSafeFileName(fileName)
            ? typeof(TemplateSeeder).Assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            : null;

    /// <summary>For tests: every house template's id and the files it needs.</summary>
    public static IReadOnlyDictionary<string, string[]> HouseAssetsForTesting() =>
        AssetsByTemplate.Value;

    private sealed record Definition(
        string Id, string Name, string Description, TemplateCategory Category,
        DateTime ReleasedUtc, string[] Tags,
        string Background, string ShapesJson, string[] Assets);

    private static IReadOnlyList<Definition> LoadDefinitions()
    {
        using var stream = typeof(TemplateSeeder).Assembly
            .GetManifestResourceStream(ResourcePrefix + "templates.json")
            ?? throw new InvalidOperationException(
                "HouseTemplates/templates.json is not embedded in Acme.Infrastructure.");
        using var doc = JsonDocument.Parse(stream);

        return doc.RootElement.EnumerateArray().Select(t => new Definition(
            t.GetProperty("id").GetString()!,
            t.GetProperty("name").GetString()!,
            t.GetProperty("description").GetString()!,
            // Strict: a misspelt category would otherwise file a template
            // somewhere nobody filters for. Failing here fails the seeding
            // tests, not a pharmacist's search.
            Enum.Parse<TemplateCategory>(t.GetProperty("category").GetString()!, ignoreCase: false),
            DateTime.SpecifyKind(
                DateTime.ParseExact(t.GetProperty("createdAt").GetString()!, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture),
                DateTimeKind.Utc),
            t.GetProperty("tags").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            t.GetProperty("background").GetString()!,
            // Raw text, not re-serialised: the shapes are the generator's
            // output exactly, including properties this side does not model.
            t.GetProperty("shapes").GetRawText(),
            t.GetProperty("assets").EnumerateArray().Select(x => x.GetString()!).ToArray()))
            .ToList();
    }
}
