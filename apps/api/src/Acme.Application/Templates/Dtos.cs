using System.Text.Json;
using Acme.Domain.Templates;

namespace Acme.Application.Templates;

public sealed record TemplateSummaryDto(
    string Id,
    string Name,
    string Description,
    TemplateCategory Category,
    IReadOnlyList<string> Tags,
    string? ThumbnailUrl,
    /// <summary>
    /// Full canvas document — used by the web client to render an inline SVG
    /// preview on the templates list. Small enough for our 6 system templates;
    /// revisit if the catalog ever grows large.
    /// </summary>
    JsonElement Canvas,
    bool IsPublished,
    DateTime UpdatedAtUtc);

public sealed record TemplateDto(
    string Id,
    string Name,
    string Description,
    TemplateCategory Category,
    IReadOnlyList<string> Tags,
    string? ThumbnailBlobKey,
    string? ThumbnailUrl,
    JsonElement Canvas,
    bool IsPublished,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertTemplateRequest(
    string Name,
    string Description,
    TemplateCategory Category,
    IReadOnlyList<string> Tags,
    JsonElement Canvas,
    bool IsPublished);
