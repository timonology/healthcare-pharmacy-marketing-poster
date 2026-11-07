using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Application.Templates;
using Acme.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Ai;

/// <summary>
/// A pharmacy describes a poster; a model designs it; the pharmacy owns it.
/// </summary>
/// <remarks>
/// <para>
/// The same generator the admin console uses — <see cref="ITemplateGenerator"/>
/// — with the things a paying customer needs wrapped around it: an allowance,
/// a rate limit, their own pharmacy details filled in, and a poster at the end
/// rather than a template.
/// </para>
/// <para>
/// <b>Why this is not just the admin endpoint with a different role check.</b>
/// An administrator generating a template is spending our money on our library,
/// once, deliberately. A pharmacist generating a poster is spending our money
/// on their poster, repeatedly, and against an allowance we have printed on a
/// pricing page. Those are different transactions and only one of them needs
/// counting, so they are different call paths over one generator.
/// </para>
/// </remarks>
public sealed class PosterAiService(
    IUserRepository users,
    IAiGenerationRepository generations,
    ITemplateGenerator generator,
    IBrandKitRepository brandKits,
    PosterService posters,
    ITemplateAssetService assets,
    ILogger<PosterAiService> log)
{
    /// <summary>
    /// The shortest brief worth sending to a model.
    /// </summary>
    /// <remarks>
    /// Matches the admin path's rule. "flu" is not a brief; it produces a
    /// generic poster and consumes an allowance the pharmacist will feel
    /// cheated by.
    /// </remarks>
    public const int MinPromptLength = 8;

    /// <summary>Longest brief accepted, to bound the prompt we pay for.</summary>
    public const int MaxPromptLength = 1000;

    /// <summary>
    /// How many generations one pharmacy may start in <see cref="RateWindow"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not the allowance — it is what stops a script spending the
    /// allowance in four seconds, and what stops an unlimited Pro account
    /// becoming an unlimited bill. A generation takes tens of seconds and a
    /// person considers the result before asking again, so a human never
    /// reaches this; a loop reaches it immediately.
    /// </para>
    /// <para>
    /// Deliberately above 1: "try another" is a legitimate thing to press twice
    /// in a row when the first design missed, and a limit that punishes the
    /// normal retry teaches people the feature is flaky.
    /// </para>
    /// </remarks>
    public const int MaxPerWindow = 5;

    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Designs a poster for this pharmacy and saves it as theirs.
    /// </summary>
    public async Task<Result<GeneratedPosterDto>> GenerateAsync(
        string ownerId,
        GeneratePosterRequest request,
        string bearerToken,
        CancellationToken ct)
    {
        var prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length < MinPromptLength)
            return Result<GeneratedPosterDto>.Invalid(
                "Describe the poster you want in a sentence or two.");
        if (prompt.Length > MaxPromptLength)
            return Result<GeneratedPosterDto>.Invalid(
                $"Keep the description under {MaxPromptLength} characters.");

        // The photo choice is checked before anything is counted: a bad pick
        // is the pharmacist's to fix, and costs them nothing.
        var photo = request.Photo ?? new PosterPhoto(PosterPhotoMode.Stock);
        // The pharmacy's own private uploads (SEC-15), or a gallery asset.
        if (photo.Problem(url => PharmacyPhotos.TryFileName(url, out _) || assets.IsAssetUrl(url)) is { } photoProblem)
            return Result<GeneratedPosterDto>.Invalid(photoProblem);

        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<GeneratedPosterDto>.NotFound("User not found.");

        var plan = Plans.For(user.Tier);

        // Every refusal that can be known up front happens up front, because
        // refusing is free and the model call is not.
        //
        // The poster limit is one of them, and it is checked here as well as at
        // save time on purpose: a Free pharmacy holding 5 of 5 posters would
        // otherwise pay an AI design to be told, afterwards, that there is
        // nowhere to put it. The comment on CreateFromGeneratedAsync used to
        // claim this check existed before it did.
        if (await posters.WhyCannotCreateAsync(ownerId, ct) is { } noRoom)
            return Result<GeneratedPosterDto>.Forbidden(noRoom);

        if (await RefuseForAllowance(plan, ownerId, ct) is { } overAllowance)
            return overAllowance;
        if (await RefuseForRate(ownerId, ct) is { } tooFast)
            return tooFast;

        // Recorded before the call, not after.
        //
        // The rate limit counts these records, so writing them only on success
        // meant a run of failures wrote nothing, the limiter read zero, and a
        // loop that failed every time was never limited — paying for a model
        // call each time round. An attempt that never completes is
        // rate-limited and never billed, which is the safe direction for both.
        var attempt = AiGeneration.Attempt(
            ownerId, AiGenerationKind.PosterDesign, prompt, DateTime.UtcNow);
        await generations.AddAsync(attempt, ct);

        var design = await generator.GenerateAsync(
            new GenerateTemplateRequest(
                Prompt: prompt,
                Category: request.Category,
                Palette: request.Palette,
                // The same four photo choices an administrator has. One
                // generation is one AI design whatever the photo: stock and
                // uploads cost nothing, and an AI photo is made inside this
                // same call under this same allowance — never a second charge,
                // and there is no standalone "make me a picture" for a
                // pharmacy that could be pressed again and again.
                Photo: photo,
                // Uploads are read from this pharmacy's own area and nowhere else.
                OwnerId: ownerId),
            bearerToken,
            ct,
            // Signed only after both checks above passed. That ordering is the
            // guarantee: ai-service will not design a poster for a non-staff
            // caller without one, and one only exists downstream of an
            // allowance check.
            AiQuotaAssertion.Sign(
                ownerId, AiQuotaAssertion.PosterDesignPurpose, DateTimeOffset.UtcNow));

        if (!design.IsSuccess)
        {
            // The attempt stays on record; it is simply never marked as
            // succeeded, so it counts against the rate limit and not against
            // the allowance. A pharmacist does not lose a design to our outage,
            // and a loop of failures still meets a limiter.
            log.LogWarning(
                "AI poster generation failed for {OwnerId}: {Error}",
                ownerId, design.Error);
            return Result<GeneratedPosterDto>.Invalid(
                design.Error ?? "We could not design that poster. Try rewording it.");
        }

        var generated = design.Value!;

        // Charged now, before the poster is saved, and this order is
        // deliberate. The model call has already happened and already cost
        // money; marking it afterwards would mean a save failure left us having
        // paid and counted nothing, which is the direction that loses money
        // quietly. The worst case this way is a pharmacist who spent an
        // allowance and can see exactly why.
        attempt.Succeed(generated.Name);
        await generations.MarkSucceededAsync(attempt, ct);

        // The pharmacy's own details, filled into the generated design.
        //
        // The generator writes {{pharmacy_name}} and friends exactly as the
        // built-in templates do, and PosterService only resolves those on the
        // from-a-template path. A generated canvas goes in as raw JSON, so
        // without this the pharmacist opens their new poster and reads
        // "{{address}}" across the bottom.
        var owner = user;
        var kit = await brandKits.FindByOwnerAsync(ownerId, ct);
        var canvasJson = BrandCanvasAssets.ResolveTokensForNewPoster(
            generated.CanvasJson, owner.Profile, kit);

        // Then the logo, where the house style leaves room for it. Only when
        // there is one: an empty slot is a box the pharmacist has to find and
        // delete. The booking QR needs nothing here — the design always carries
        // the slot, and every renderer draws nothing in it until a booking
        // link exists.
        if (!string.IsNullOrWhiteSpace(kit?.LogoBlobKey))
            canvasJson = BrandCanvasAssets.PlaceLogo(canvasJson);

        var created = await posters.CreateFromGeneratedAsync(
            ownerId, generated.Name, canvasJson, ct);

        if (!created.IsSuccess)
            return created.Kind == ResultKind.Forbidden
                ? Result<GeneratedPosterDto>.Forbidden(created.Error!)
                : Result<GeneratedPosterDto>.Invalid(created.Error ?? "The poster could not be saved.");

        log.LogInformation(
            "AI poster \"{Name}\" generated for {OwnerId} on {Plan}",
            generated.Name, ownerId, plan.DisplayName);

        // What happened to the photo, if it was not what was asked for. Shown
        // to the pharmacist before they open the poster: a stock search that
        // found nothing must not quietly become a poster with no photo.
        return Result<GeneratedPosterDto>.Success(
            new GeneratedPosterDto(created.Value!, generated.PhotoNotes ?? []));
    }

    /// <summary>Reads the counter; <see cref="AiAllowance"/> decides.</summary>
    private async Task<Result<GeneratedPosterDto>?> RefuseForAllowance(
        PlanDefinition plan, string ownerId, CancellationToken ct)
    {
        // Counted only when the plan has a finite allowance. An unlimited plan
        // asking "how many this month" is a query per generation that cannot
        // change the answer.
        var used = plan.MaxAiGenerationsPerMonth == Plans.Unlimited
            ? 0
            : await generations.CountSucceededThisMonthAsync(ownerId, ct);

        return AiAllowance.Refuse(plan, used) is { } why
            ? Result<GeneratedPosterDto>.Forbidden(why)
            : null;
    }

    /// <summary>Refuses a burst, without spending the month's allowance on it.</summary>
    private async Task<Result<GeneratedPosterDto>?> RefuseForRate(string ownerId, CancellationToken ct)
    {
        var since = DateTime.UtcNow - RateWindow;
        var recent = await generations.CountAttemptsSinceAsync(ownerId, since, ct);

        if (AiAllowance.RefuseForRate(recent, MaxPerWindow) is not { } why) return null;

        log.LogWarning(
            "Rate limit hit for {OwnerId}: {Count} AI generations within {Seconds}s.",
            ownerId, recent, RateWindow.TotalSeconds);

        // Deliberately not Forbidden. Forbidden reads as "your plan does not
        // allow this" and would send a paying customer to the pricing page to
        // fix something that fixes itself in under a minute.
        return Result<GeneratedPosterDto>.Invalid(why);
    }
}

/// <summary>A designed poster, and anything the pharmacist should know about its photo.</summary>
public sealed record GeneratedPosterDto(PosterDto Poster, IReadOnlyList<string> PhotoNotes);

/// <summary>A pharmacy's brief for a poster.</summary>
/// <param name="Prompt">What the poster should say and be for, in plain English.</param>
/// <param name="Category">Optional hint, e.g. "vaccination".</param>
/// <param name="Palette">Ignored: the house palette is fixed. Accepted so older clients are not refused.</param>
/// <param name="Photo">
/// Where the photograph comes from. Null means stock — the default when the
/// pharmacy does not upload one.
/// </param>
public sealed record GeneratePosterRequest(
    string Prompt,
    string? Category = null,
    string? Palette = null,
    PosterPhoto? Photo = null);
