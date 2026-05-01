using System.Text.Json;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Templates;
using Acme.Domain.Common;
using Acme.Domain.Posters;

namespace Acme.Application.Posters;

public sealed class PosterService(
    IPosterRepository posters,
    ITemplateRepository templates,
    IBrandAssetService assets)
{
    private static readonly TimeSpan ThumbnailUrlValidity = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<PosterSummaryDto>> ListAsync(
        PosterQuery query,
        CancellationToken ct)
    {
        var items = await posters.ListAsync(query, ct);
        return items.Select(ToSummary).ToList();
    }

    public async Task<int> CountAsync(PosterQuery query, CancellationToken ct) =>
        await posters.CountAsync(query, ct);

    public async Task<Result<PosterDto>> GetAsync(string id, string ownerId, CancellationToken ct)
    {
        var p = await posters.FindByIdAsync(id, ct);
        if (p is null) return Result<PosterDto>.NotFound("Poster not found.");
        if (p.OwnerId != ownerId) return Result<PosterDto>.NotFound("Poster not found.");
        return Result<PosterDto>.Success(ToDetail(p));
    }

    public async Task<Result<PosterDto>> CreateAsync(
        string ownerId,
        CreatePosterRequest request,
        CancellationToken ct)
    {
        string canvasJson;
        string? sourceTemplateId = null;

        if (!string.IsNullOrWhiteSpace(request.FromTemplateId))
        {
            var template = await templates.FindByIdAsync(request.FromTemplateId, ct);
            if (template is null)
                return Result<PosterDto>.NotFound("Source template not found.");
            canvasJson = template.CanvasJson;
            sourceTemplateId = template.Id;
        }
        else if (request.Canvas is { } canvas)
        {
            canvasJson = canvas.GetRawText();
        }
        else
        {
            canvasJson = EmptyCanvasJson(ownerId);
        }

        Poster poster;
        try
        {
            poster = Poster.Create(ownerId, request.Name, canvasJson, sourceTemplateId);
        }
        catch (DomainException ex) { return Result<PosterDto>.Invalid(ex.Message); }

        await posters.AddAsync(poster, ct);
        return Result<PosterDto>.Success(ToDetail(poster));
    }

    public async Task<Result<PosterDto>> UpdateAsync(
        string id,
        string ownerId,
        UpdatePosterRequest request,
        CancellationToken ct)
    {
        var poster = await posters.FindByIdAsync(id, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<PosterDto>.NotFound("Poster not found.");

        try
        {
            poster.Rename(request.Name);
            poster.UpdateCanvas(request.Canvas.GetRawText());
        }
        catch (DomainException ex) { return Result<PosterDto>.Invalid(ex.Message); }

        await posters.UpdateAsync(poster, ct);
        return Result<PosterDto>.Success(ToDetail(poster));
    }

    public async Task<Result<PosterDto>> DuplicateAsync(
        string id,
        string ownerId,
        CancellationToken ct)
    {
        var source = await posters.FindByIdAsync(id, ct);
        if (source is null || source.OwnerId != ownerId)
            return Result<PosterDto>.NotFound("Poster not found.");

        var copy = Poster.Create(
            ownerId,
            $"{source.Name} (copy)",
            source.CanvasJson,
            source.SourceTemplateId);
        await posters.AddAsync(copy, ct);
        return Result<PosterDto>.Success(ToDetail(copy));
    }

    public async Task<Result<bool>> DeleteAsync(string id, string ownerId, CancellationToken ct)
    {
        var poster = await posters.FindByIdAsync(id, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<bool>.NotFound("Poster not found.");
        await posters.DeleteAsync(id, ct);
        return Result<bool>.Success(true);
    }

    public async Task<Result<PosterDto>> SetStatusAsync(
        string id,
        string ownerId,
        PosterStatus status,
        CancellationToken ct)
    {
        var poster = await posters.FindByIdAsync(id, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<PosterDto>.NotFound("Poster not found.");

        switch (status)
        {
            case PosterStatus.Published: poster.Publish(); break;
            case PosterStatus.Archived: poster.Archive(); break;
            case PosterStatus.Draft: poster.Restore(); break;
        }
        await posters.UpdateAsync(poster, ct);
        return Result<PosterDto>.Success(ToDetail(poster));
    }

    private PosterSummaryDto ToSummary(Poster p) => new(
        p.Id,
        p.OwnerId,
        p.Name,
        p.SourceTemplateId,
        ThumbnailUrl(p.ThumbnailBlobKey),
        ParseCanvas(p.CanvasJson),
        p.Status,
        p.CreatedAtUtc,
        p.UpdatedAtUtc);

    private PosterDto ToDetail(Poster p) => new(
        p.Id,
        p.OwnerId,
        p.Name,
        p.SourceTemplateId,
        p.ThumbnailBlobKey,
        ThumbnailUrl(p.ThumbnailBlobKey),
        ParseCanvas(p.CanvasJson),
        p.Status,
        p.CreatedAtUtc,
        p.UpdatedAtUtc);

    private string? ThumbnailUrl(string? blobKey) =>
        blobKey is null ? null : assets.GetReadOnlyUrl(blobKey, ThumbnailUrlValidity).ToString();

    private static JsonElement ParseCanvas(string canvasJson)
    {
        using var doc = JsonDocument.Parse(canvasJson);
        return doc.RootElement.Clone();
    }

    private static string EmptyCanvasJson(string ownerId)
    {
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("N");
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "ownerId": "{{ownerId}}",
          "name": "Untitled",
          "width": 1920,
          "height": 1080,
          "background": "#ffffff",
          "viewport": { "pan": { "x": 0, "y": 0 }, "zoom": 1 },
          "shapes": [],
          "createdAtUtc": "{{now}}",
          "updatedAtUtc": "{{now}}"
        }
        """;
    }
}
