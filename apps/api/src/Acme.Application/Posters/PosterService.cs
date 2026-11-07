using System.Text.Json;
using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Subscriptions;
using Acme.Application.Templates;
using Acme.Domain.Common;
using Acme.Domain.Posters;

namespace Acme.Application.Posters;

public sealed class PosterService(
    IPosterRepository posters,
    ITemplateRepository templates,
    IUserRepository users,
    IBrandKitRepository brandKits,
    Microsoft.Extensions.Options.IOptions<CanvasLimitsOptions> canvasLimits)
{
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
        if (await EnforcePosterLimit(ownerId, ct) is { } limitFailure)
            return limitFailure;

        string canvasJson;
        string? sourceTemplateId = null;

        if (!string.IsNullOrWhiteSpace(request.FromTemplateId))
        {
            var template = await templates.FindByIdAsync(request.FromTemplateId, ct);
            if (template is null)
                return Result<PosterDto>.NotFound("Source template not found.");

            // The copy takes the pharmacy's details with it.
            //
            // Templates write their contact line as {{address}} style tokens so
            // that applying the details fills them in place rather than adding a
            // second contact block. The renderer resolves those, so a published
            // poster is correct either way — but the editor draws the canvas as
            // it is stored, and a designer opening a new poster to find
            // "{{address}}" across the bottom has been handed something that
            // looks broken.
            //
            // Filling them here means the tokens exist in the template, which is
            // the only place they are useful, and never in a poster.
            // The kit as well as the profile: {{booking_url}} resolves from the
            // brand kit now, and a new poster whose booking line came out blank
            // would look like the template was broken.
            var owner = await users.FindByIdAsync(ownerId, ct);
            var kit = await brandKits.FindByOwnerAsync(ownerId, ct);
            canvasJson = BrandCanvasAssets.ResolveTokensForNewPoster(
                template.CanvasJson, owner?.Profile, kit);

            sourceTemplateId = template.Id;
        }
        else if (request.Canvas is { } canvas)
        {
            canvasJson = canvas.GetRawText();
            if (CanvasLimits.WhyTooLarge(canvasJson, canvasLimits.Value) is { } tooLarge)
                return Result<PosterDto>.Invalid(tooLarge);
            if (CanvasImageSources.WhyRefused(canvasJson) is { } refused)
                return Result<PosterDto>.Invalid(refused);
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

    /// <summary>
    /// Saves an AI-designed canvas as a poster this pharmacy owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="CreateAsync"/> because the canvas arrives as a
    /// string that has already had the pharmacy's tokens resolved, rather than
    /// as a <c>JsonElement</c> off a request body. Routing it through
    /// <c>CreateAsync</c> would mean re-parsing JSON we just produced purely to
    /// satisfy a DTO shape.
    /// </para>
    /// <para>
    /// The poster limit is enforced here as well, and that is not belt and
    /// braces: <c>PosterAiService</c> asks <see cref="WhyCannotCreateAsync"/>
    /// before spending on a model, but a second tab can fill the last slot
    /// while the design is being generated. The pharmacist is told plainly, and
    /// the allowance they spent stays spent, because the money was.
    /// </para>
    /// </remarks>
    public async Task<Result<PosterDto>> CreateFromGeneratedAsync(
        string ownerId,
        string name,
        string canvasJson,
        CancellationToken ct)
    {
        if (await EnforcePosterLimit(ownerId, ct) is { } limitFailure)
            return limitFailure;

        Poster poster;
        try
        {
            poster = Poster.Create(ownerId, name, canvasJson, sourceTemplateId: null);
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

        // Refused at save, so the pharmacist hears it now, in the editor —
        // not as a missing picture on a printed poster (SEC-01, SEC-02).
        var canvasJson = request.Canvas.GetRawText();
        if (CanvasLimits.WhyTooLarge(canvasJson, canvasLimits.Value) is { } tooLarge)
            return Result<PosterDto>.Invalid(tooLarge);
        if (CanvasImageSources.WhyRefused(canvasJson) is { } refused)
            return Result<PosterDto>.Invalid(refused);

        try
        {
            poster.Rename(request.Name);
            poster.UpdateCanvas(canvasJson);
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

        if (await EnforcePosterLimit(ownerId, ct) is { } limitFailure)
            return limitFailure;

        var copy = Poster.Create(
            ownerId,
            $"{source.Name} (copy)",
            source.CanvasJson,
            source.SourceTemplateId);
        await posters.AddAsync(copy, ct);
        return Result<PosterDto>.Success(ToDetail(copy));
    }

    /// <summary>
    /// Why this pharmacy cannot save another poster, or null if it can.
    /// </summary>
    /// <remarks>
    /// Public because the answer is worth knowing <i>before</i> doing expensive
    /// work that ends in a poster. <c>PosterAiService</c> asks first, so a
    /// pharmacy at its limit is refused for free rather than after paying for a
    /// design it has nowhere to put. Returning the sentence rather than a
    /// <c>Result</c> keeps the rule in one place while letting each caller
    /// choose the shape of its own refusal.
    /// </remarks>
    public async Task<string?> WhyCannotCreateAsync(string ownerId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return null;   // "not found" is the caller's to report

        var plan = Plans.For(user.Tier);
        if (plan.MaxPosters == Plans.Unlimited) return null;

        var current = await posters.CountAsync(new PosterQuery(ownerId), ct);
        if (current < plan.MaxPosters) return null;

        return $"You've reached your {plan.DisplayName} plan limit of {plan.MaxPosters} posters. Upgrade to add more.";
    }

    private async Task<Result<PosterDto>?> EnforcePosterLimit(string ownerId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<PosterDto>.NotFound("User not found.");

        return await WhyCannotCreateAsync(ownerId, ct) is { } why
            ? Result<PosterDto>.Forbidden(why)
            : null;
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
        CardUrl(p),
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
        CardUrl(p),
        ParseCanvas(p.CanvasJson),
        p.Status,
        p.CreatedAtUtc,
        p.UpdatedAtUtc);

    /// <summary>
    /// The poster's card image, drawn by the PDF/PNG renderer
    /// (<c>GET /api/posters/{id}/card</c>, see <see cref="PosterImageService.EnsureCardAsync"/>).
    /// </summary>
    /// <remarks>
    /// Same-origin and stable, never a signed storage URL: those pointed the
    /// browser at a host it could not always reach and expired while the list
    /// was open, which drew a broken-image "?" on published posters. <c>v</c>
    /// changes on every save so the browser asks for the edited card at once;
    /// brand-kit changes are caught by the endpoint's ETag.
    /// </remarks>
    private static string CardUrl(Poster p) =>
        $"/api/posters/{Uri.EscapeDataString(p.Id)}/card?v={p.UpdatedAtUtc.Ticks}";

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
