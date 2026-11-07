using System.Text;
using System.Text.Json;

namespace Acme.Application.Posters;

/// <summary>
/// How big a saved canvas may be (security review R2-12).
/// </summary>
/// <remarks>
/// <para>
/// Every export, campaign image and display screen draws the whole canvas, so
/// one oversized poster slows the renderer for everybody on the instance. And
/// on the production database (Cosmos DB for MongoDB, RU-based — see
/// infra/azure/main.bicep) a document over 2 MB is refused with an error that
/// means nothing to a pharmacist. The default sits just under that ceiling, so
/// the pharmacist hears a plain reason in the editor instead.
/// </para>
/// <para>
/// A database without that ceiling (local MongoDB, Cosmos vCore) can raise it
/// with <c>Posters__MaxCanvasBytes</c>.
/// </para>
/// </remarks>
public sealed class CanvasLimitsOptions
{
    public const string SectionName = "Posters";

    /// <summary>Largest canvas JSON, in UTF-8 bytes. Inline pictures count.</summary>
    public int MaxCanvasBytes { get; set; } = 1_900_000;

    /// <summary>Most shapes a canvas may hold. The house templates use under 100.</summary>
    public int MaxShapes { get; set; } = 500;
}

public static class CanvasLimits
{
    /// <summary>Why this canvas is too large to save, or null when it fits.</summary>
    public static string? WhyTooLarge(string? canvasJson, CanvasLimitsOptions limits)
    {
        if (string.IsNullOrEmpty(canvasJson)) return null;

        var bytes = Encoding.UTF8.GetByteCount(canvasJson);
        if (bytes > limits.MaxCanvasBytes)
            return $"This poster is too large to save ({bytes / 1_000_000.0:0.0} MB; the limit is "
                   + $"{limits.MaxCanvasBytes / 1_000_000.0:0.0} MB). Use fewer or smaller pictures.";

        try
        {
            using var doc = JsonDocument.Parse(canvasJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Case-insensitive, like the renderer.
                if (!prop.Name.Equals("shapes", StringComparison.OrdinalIgnoreCase)
                    || prop.Value.ValueKind != JsonValueKind.Array)
                    continue;
                var count = prop.Value.GetArrayLength();
                if (count > limits.MaxShapes)
                    return $"This poster has {count} items; the limit is {limits.MaxShapes}. "
                           + "Remove some before saving.";
            }
        }
        catch (JsonException)
        {
            return "The poster could not be read.";
        }
        return null;
    }
}
