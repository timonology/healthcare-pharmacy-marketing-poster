using Acme.Domain.Common;

namespace Acme.Domain.Templates;

public sealed class Template : Entity
{
    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public TemplateCategory Category { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();

    public string? ThumbnailBlobKey { get; private set; }

    public string CanvasJson { get; private set; } = default!;

    public bool IsPublished { get; private set; }

    private Template() { }

    public static Template Create(
        string name,
        string description,
        TemplateCategory category,
        IEnumerable<string> tags,
        string canvasJson,
        bool isPublished = true) =>
        CreateWithId(
            Guid.NewGuid().ToString("N"),
            name,
            description,
            category,
            tags,
            canvasJson,
            isPublished);

    public static Template CreateWithId(
        string id,
        string name,
        string description,
        TemplateCategory category,
        IEnumerable<string> tags,
        string canvasJson,
        bool isPublished = true)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new DomainException("Id is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Template name is required.");
        if (name.Length > 200)
            throw new DomainException("Template name must be 200 chars or fewer.");
        if (string.IsNullOrWhiteSpace(canvasJson))
            throw new DomainException("Canvas JSON is required.");

        return new Template
        {
            Id = id,
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            Category = category,
            Tags = NormalizeTags(tags),
            CanvasJson = canvasJson,
            IsPublished = isPublished,
        };
    }

    public void Update(
        string name,
        string description,
        TemplateCategory category,
        IEnumerable<string> tags,
        string canvasJson)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Template name is required.");
        if (string.IsNullOrWhiteSpace(canvasJson))
            throw new DomainException("Canvas JSON is required.");

        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        Category = category;
        Tags = NormalizeTags(tags);
        CanvasJson = canvasJson;
        Touch();
    }

    public void SetThumbnail(string blobKey)
    {
        if (string.IsNullOrWhiteSpace(blobKey))
            throw new DomainException("Blob key is required.");
        ThumbnailBlobKey = blobKey;
        Touch();
    }

    public void Publish() { IsPublished = true; Touch(); }
    public void Unpublish() { IsPublished = false; Touch(); }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .Take(20)
            .ToList();
}
