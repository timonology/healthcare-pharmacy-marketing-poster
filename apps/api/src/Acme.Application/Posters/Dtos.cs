using System.Text.Json;
using Acme.Domain.Posters;

namespace Acme.Application.Posters;

public sealed record PosterSummaryDto(
    string Id,
    string OwnerId,
    string Name,
    string? SourceTemplateId,
    string? ThumbnailUrl,
    /// <summary>
    /// Full canvas — used for inline SVG previews on the My Posters list.
    /// For very large libraries, consider rendering thumbnails to blob storage
    /// instead of returning the canvas on every list call.
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
