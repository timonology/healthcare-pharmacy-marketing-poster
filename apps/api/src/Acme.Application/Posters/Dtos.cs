using System.Text.Json;
using Acme.Domain.Posters;

namespace Acme.Application.Posters;

public sealed record PosterSummaryDto(
    string Id,
    string OwnerId,
    string Name,
    string? SourceTemplateId,
    /// <summary>
    /// The card image: <c>/api/posters/{id}/card?v=…</c>, drawn by the PDF/PNG
    /// renderer and served same-origin. Never a storage URL.
    /// </summary>
    string? ThumbnailUrl,
    /// <summary>
    /// Full canvas — drawn in the browser only when the card image cannot load.
    /// </summary>
    JsonElement Canvas,
    PosterStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record PosterDto(
    string Id,
    string OwnerId,
    string Name,
    string? SourceTemplateId,
    string? ThumbnailBlobKey,
    string? ThumbnailUrl,
    JsonElement Canvas,
    PosterStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>Create a poster either blank or by cloning a template.</summary>
public sealed record CreatePosterRequest(
    string Name,
    string? FromTemplateId,
    JsonElement? Canvas);

public sealed record UpdatePosterRequest(string Name, JsonElement Canvas);
