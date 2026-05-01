using Acme.Domain.Common;

namespace Acme.Domain.Posters;

/// <summary>
/// A user-owned poster. Either created blank or cloned from a template.
/// </summary>
public sealed class Poster : Entity
{
    public string OwnerId { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    /// <summary>The template this poster was cloned from. Null when created blank.</summary>
    public string? SourceTemplateId { get; private set; }

    public string? ThumbnailBlobKey { get; private set; }

    public string CanvasJson { get; private set; } = default!;
    public PosterStatus Status { get; private set; } = PosterStatus.Draft;

    private Poster() { }

    public static Poster Create(
        string ownerId,
        string name,
        string canvasJson,
        string? sourceTemplateId = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Poster name is required.");
        if (name.Length > 200)
            throw new DomainException("Poster name must be 200 chars or fewer.");
        if (string.IsNullOrWhiteSpace(canvasJson))
            throw new DomainException("Canvas JSON is required.");

        return new Poster
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Name = name.Trim(),
            CanvasJson = canvasJson,
            SourceTemplateId = sourceTemplateId,
        };
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Poster name is required.");
        Name = name.Trim();
        Touch();
    }

    public void UpdateCanvas(string canvasJson)
    {
        if (string.IsNullOrWhiteSpace(canvasJson))
            throw new DomainException("Canvas JSON is required.");
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

    public void Publish()
    {
        Status = PosterStatus.Published;
        Touch();
    }

    public void Archive()
    {
        Status = PosterStatus.Archived;
        Touch();
    }

    public void Restore()
    {
        Status = PosterStatus.Draft;
        Touch();
    }
}
