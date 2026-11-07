namespace Acme.Application.Export;

/// <param name="CanvasJson">The canvas document to draw.</param>
/// <param name="TargetWidthPx">
/// Width of the output in pixels; height follows the canvas aspect ratio.
/// Null renders at the canvas's own width.
/// </param>
/// <param name="IncludeWatermark">
/// Applies the same watermark rule as PDF export, so a Free-tier poster is
/// marked wherever it ends up.
/// </param>
public sealed record RasterRequest(
    string CanvasJson,
    int? TargetWidthPx = null,
    bool IncludeWatermark = false);

/// <summary>
/// Renders a canvas document to a raster image.
/// </summary>
/// <remarks>
/// <para>
/// Separate interface from <see cref="IPosterPdfExporter"/> even though one
/// class implements both: a caller that needs a PNG has no business being able
/// to ask for a PDF, and the two have genuinely different consumers — print on
/// one side, social platforms on the other.
/// </para>
/// <para>
/// This exists because <b>nothing in the system stores a rendered poster</b>.
/// The canvas JSON is the source of truth and every view is regenerated from
/// it: previews are SVG drawn in the browser, PDFs are drawn per request. That
/// is fine until something outside the product needs an actual image file —
/// Facebook and Instagram fetch media by URL and cannot be handed a JSON
/// document.
/// </para>
/// </remarks>
public interface ICanvasRasterizer
{
    /// <summary>Renders to PNG bytes.</summary>
    Task<byte[]> RenderPngAsync(RasterRequest request, CancellationToken ct);
}
