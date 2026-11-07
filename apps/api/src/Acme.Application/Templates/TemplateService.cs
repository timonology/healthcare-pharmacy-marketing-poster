using System.Text.Json;
using Acme.Application.Audit;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Domain.Common;
using Acme.Domain.Templates;

namespace Acme.Application.Templates;

public sealed class TemplateService(
    ITemplateRepository repo,
    IBrandAssetService assets,
    IAuditScope audit,
    Microsoft.Extensions.Options.IOptions<Posters.CanvasLimitsOptions> canvasLimits)
{
    private static readonly TimeSpan ThumbnailUrlValidity = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<TemplateSummaryDto>> ListAsync(
        TemplateQuery query,
        CancellationToken ct)
    {
        var items = await repo.ListAsync(query, ct);
        return items.Select(ToSummary).ToList();
    }

    public async Task<int> CountAsync(TemplateQuery query, CancellationToken ct) =>
        await repo.CountAsync(query, ct);

    public async Task<Result<TemplateDto>> GetAsync(string id, CancellationToken ct)
    {
        var t = await repo.FindByIdAsync(id, ct);
        return t is null
            ? Result<TemplateDto>.NotFound("Template not found.")
            : Result<TemplateDto>.Success(ToDetail(t));
    }

    public async Task<Result<TemplateDto>> CreateAsync(
        UpsertTemplateRequest request,
        CancellationToken ct)
    {
        // A template is drawn into every pharmacy's posters: the same image
        // rule as a poster (SEC-01, SEC-02).
        if (Posters.CanvasLimits.WhyTooLarge(request.Canvas.GetRawText(), canvasLimits.Value) is { } tooLarge)
            return Result<TemplateDto>.Invalid(tooLarge);
        if (Posters.CanvasImageSources.WhyRefused(request.Canvas.GetRawText()) is { } refused)
            return Result<TemplateDto>.Invalid(refused);

        Template tpl;
        try
        {
            tpl = Template.Create(
                request.Name,
                request.Description,
                request.Category,
                request.Tags ?? Array.Empty<string>(),
                request.Canvas.GetRawText(),
                request.IsPublished);
        }
        catch (DomainException ex) { return Result<TemplateDto>.Invalid(ex.Message); }

        await repo.AddAsync(tpl, ct);
        audit.SetEntity("template", tpl.Id);
        audit.AddMeta("published", tpl.IsPublished);
        return Result<TemplateDto>.Success(ToDetail(tpl));
    }

    public async Task<Result<TemplateDto>> UpdateAsync(
        string id,
        UpsertTemplateRequest request,
        CancellationToken ct)
    {
        var tpl = await repo.FindByIdAsync(id, ct);
        if (tpl is null) return Result<TemplateDto>.NotFound("Template not found.");

        if (Posters.CanvasLimits.WhyTooLarge(request.Canvas.GetRawText(), canvasLimits.Value) is { } tooLarge)
            return Result<TemplateDto>.Invalid(tooLarge);
        if (Posters.CanvasImageSources.WhyRefused(request.Canvas.GetRawText()) is { } refusedSource)
            return Result<TemplateDto>.Invalid(refusedSource);

        // Publishing is what puts a template in front of every pharmacy, so it
        // is named as such rather than hidden inside "update".
        var wasPublished = tpl.IsPublished;
        if (request.IsPublished != wasPublished)
            audit.SetAction(request.IsPublished ? "admin.template.publish" : "admin.template.unpublish");

        try
        {
            tpl.Update(
                request.Name,
                request.Description,
                request.Category,
                request.Tags ?? Array.Empty<string>(),
                request.Canvas.GetRawText());
            if (request.IsPublished) tpl.Publish(); else tpl.Unpublish();
        }
        catch (DomainException ex) { return Result<TemplateDto>.Invalid(ex.Message); }

        await repo.UpdateAsync(tpl, ct);
        return Result<TemplateDto>.Success(ToDetail(tpl));
    }

    public async Task<Result<bool>> DeleteAsync(string id, CancellationToken ct)
    {
        var tpl = await repo.FindByIdAsync(id, ct);
        if (tpl is null) return Result<bool>.NotFound("Template not found.");
        await repo.DeleteAsync(id, ct);
        return Result<bool>.Success(true);
    }

    private TemplateSummaryDto ToSummary(Template t) => new(
        t.Id,
        t.Name,
        t.Description,
        t.Category,
        t.Tags,
        ThumbnailUrl(t.ThumbnailBlobKey),
        ParseCanvas(t.CanvasJson),
        t.IsPublished,
        t.UpdatedAtUtc);

    private TemplateDto ToDetail(Template t) => new(
        t.Id,
        t.Name,
        t.Description,
        t.Category,
        t.Tags,
        t.ThumbnailBlobKey,
        ThumbnailUrl(t.ThumbnailBlobKey),
        ParseCanvas(t.CanvasJson),
        t.IsPublished,
        t.CreatedAtUtc,
        t.UpdatedAtUtc);

    private string? ThumbnailUrl(string? blobKey) =>
        blobKey is null ? null : assets.GetReadOnlyUrl(blobKey, ThumbnailUrlValidity).ToString();

    private static JsonElement ParseCanvas(string canvasJson)
    {
        using var doc = JsonDocument.Parse(canvasJson);
        return doc.RootElement.Clone();
    }
}
