namespace Acme.Application.Export;

public sealed record PdfExportRequest(
    string CanvasJson,
    string PosterName,
    bool IncludeWatermark);

/// <param name="Route">
/// Which resolution branch the key takes: "template-asset/blob", "remote-url",
/// "data-uri", "bare-blob-key", or a marker naming what is missing.
/// </param>
/// <param name="Error">Null when the image loaded.</param>
public sealed record ImageProbe(
    string BlobKey,
    string Route,
    bool Ok,
    string? Error,
    int Bytes);

public interface IPosterPdfExporter
{
    Task<byte[]> RenderAsync(PdfExportRequest request, CancellationToken ct);

    /// <summary>
    /// Reports whether each image in a canvas can actually be loaded.
    /// </summary>
    /// <remarks>
    /// Deliberately part of the exporter rather than a separate diagnostic
    /// helper: it runs the same fetch the renderer runs, so it cannot drift
    /// from reality and report success on something the PDF then drops. A
    /// reimplementation would eventually disagree, which is worse than no
    /// diagnostic at all.
    ///
    /// The renderer swallows image failures on purpose — a poster missing its
    /// photograph is better than no poster — but that makes the failure
    /// invisible. This is how you see it.
    /// </remarks>
    Task<IReadOnlyList<ImageProbe>> ProbeImagesAsync(string canvasJson, CancellationToken ct);
}
