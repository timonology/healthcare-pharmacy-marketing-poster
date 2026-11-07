using Acme.Application.Common;
using Acme.Application.Templates;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Ai;

/// <summary>A photo a pharmacy uploaded, stored and ready to design around.</summary>
/// <param name="Url">Our own asset URL — the only kind the designer accepts.</param>
public sealed record StoredPosterPhoto(string Url, string ContentType);

/// <summary>
/// The photo side of "Design with AI" for a pharmacy: the same choices an
/// administrator has — stock, upload, AI — with the guards a paying customer's
/// path needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of it is billed.</b> Searching stock and uploading cost nothing,
/// and an AI photograph is made inside the generation itself
/// (<see cref="PosterAiService"/>), under the one allowance a design already
/// spends. There is deliberately no pharmacy endpoint that makes a picture on
/// its own: that would be a second, repeatable charge.
/// </para>
/// <para>
/// <b>Stock search is rate-limited, then vouched for.</b> The Pexels key is
/// shared by every pharmacy and its hourly allowance is the thing a loop would
/// exhaust for everybody. The controller's rate limiter stops the loop; the
/// signed assertion is how ai-service knows the API did.
/// </para>
/// </remarks>
public sealed class PosterPhotoService(
    ITemplateGenerator generator,
    IPharmacyPhotoStore uploads,
    ILogger<PosterPhotoService> log)
{
    /// <summary>The same 12MB as the admin upload. The browser downscales well below it.</summary>
    public const long MaxUploadBytes = 12L * 1024 * 1024;

    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 80;

    /// <summary>
    /// Stores an upload after checking it is really a JPEG, PNG or WebP.
    /// </summary>
    /// <remarks>
    /// The bytes decide, not the browser's content type — see
    /// <see cref="ImageSniffer"/>. A bad file is refused here, at upload, so
    /// the pharmacist hears about it before they have pressed Generate.
    /// </remarks>
    public async Task<Result<StoredPosterPhoto>> UploadAsync(
        string ownerId, Stream content, long length, CancellationToken ct)
    {
        if (length <= 0)
            return Result<StoredPosterPhoto>.Invalid("Choose a photo to upload.");
        if (length > MaxUploadBytes)
            return Result<StoredPosterPhoto>.Invalid("That photo is larger than 12MB.");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxUploadBytes)
            return Result<StoredPosterPhoto>.Invalid("That photo is larger than 12MB.");

        if (Sniff(buffer) is not { } contentType)
            return Result<StoredPosterPhoto>.Invalid(
                "That file isn't a JPEG, PNG or WebP photo. Choose a different file.");

        buffer.Position = 0;
        var now = DateTime.UtcNow;
        var fileName = PharmacyPhotos.NewFileName(now, contentType);
        await uploads.SaveAsync(ownerId, fileName, buffer, contentType, ct);
        log.LogInformation("Poster photo uploaded: {Kb} KB {Type}", buffer.Length / 1024, contentType);

        // Housekeeping for this pharmacy only, on the request that adds to its
        // pile: uploads past their use are deleted (SEC-15). Best effort — a
        // failure here must not cost the pharmacist the photo just uploaded.
        try
        {
            var removed = await uploads.DeleteOlderThanAsync(ownerId, now - PharmacyPhotos.KeptFor, ct);
            if (removed > 0) log.LogInformation("Removed {Count} expired poster photo upload(s)", removed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not remove expired poster photo uploads");
        }

        return Result<StoredPosterPhoto>.Success(
            new StoredPosterPhoto(PharmacyPhotos.UrlFor(fileName), contentType));
    }

    /// <summary>One of the caller's own uploads, or null — never another pharmacy's.</summary>
    public Task<Stream?> OpenUploadAsync(string ownerId, string fileName, CancellationToken ct) =>
        uploads.OpenAsync(ownerId, fileName, ct);

    /// <summary>
    /// A separate method because a span cannot live in an async one.
    /// </summary>
    private static string? Sniff(MemoryStream buffer) =>
        ImageSniffer.Detect(buffer.GetBuffer().AsSpan(
            0, (int)Math.Min(buffer.Length, ImageSniffer.HeaderLength)));

    /// <summary>Stock photos for the picker. Thumbnails only; nothing is stored until generation.</summary>
    public async Task<Result<IReadOnlyList<StockPhoto>>> SearchStockAsync(
        string ownerId, string? query, string? aspect, string bearerToken, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        if (q.Length < MinQueryLength)
            return Result<IReadOnlyList<StockPhoto>>.Invalid("Type what the photo should show.");
        if (q.Length > MaxQueryLength)
            return Result<IReadOnlyList<StockPhoto>>.Invalid(
                $"Keep the search under {MaxQueryLength} characters.");

        return await generator.SearchStockPhotosAsync(
            q, aspect, bearerToken, ct,
            AiQuotaAssertion.Sign(ownerId, AiQuotaAssertion.StockSearchPurpose, DateTimeOffset.UtcNow));
    }

    /// <summary>Which photo modes to offer. AI is hidden, with a reason, when no model is set up.</summary>
    public Task<Result<ImageOptions>> GetOptionsAsync(string bearerToken, CancellationToken ct) =>
        generator.GetImageOptionsAsync(bearerToken, ct);
}
