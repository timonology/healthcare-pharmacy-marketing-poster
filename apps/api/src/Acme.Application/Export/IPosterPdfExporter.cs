namespace Acme.Application.Export;

public sealed record PdfExportRequest(
    string CanvasJson,
    string PosterName,
    bool IncludeWatermark);

public interface IPosterPdfExporter
{
    Task<byte[]> RenderAsync(PdfExportRequest request, CancellationToken ct);
}
