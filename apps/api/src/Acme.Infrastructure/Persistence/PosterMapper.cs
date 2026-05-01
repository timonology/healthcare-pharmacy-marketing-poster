using System.Reflection;
using Acme.Domain.Posters;

namespace Acme.Infrastructure.Persistence;

internal static class PosterMapper
{
    private static readonly ConstructorInfo Ctor = typeof(Poster)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Poster parameterless ctor missing.");

    public static Poster Hydrate(PosterDocument d)
    {
        var p = (Poster)Ctor.Invoke(null);
        Set(p, nameof(Poster.Id), d.Id);
        Set(p, nameof(Poster.OwnerId), d.OwnerId);
        Set(p, nameof(Poster.Name), d.Name);
        Set(p, nameof(Poster.SourceTemplateId), d.SourceTemplateId);
        Set(p, nameof(Poster.ThumbnailBlobKey), d.ThumbnailBlobKey);
        Set(p, nameof(Poster.CanvasJson), CanvasJsonConverter.FromBson(d.Canvas));
        Set(p, nameof(Poster.Status), (PosterStatus)d.Status);
        Set(p, nameof(Poster.CreatedAtUtc), d.CreatedAtUtc);
        Set(p, nameof(Poster.UpdatedAtUtc), d.UpdatedAtUtc);
        return p;
    }

    public static PosterDocument ToDocument(Poster p) => new()
    {
        Id = p.Id,
        OwnerId = p.OwnerId,
        Name = p.Name,
        SourceTemplateId = p.SourceTemplateId,
        ThumbnailBlobKey = p.ThumbnailBlobKey,
        Canvas = CanvasJsonConverter.ToBson(p.CanvasJson),
        Status = (int)p.Status,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc,
    };

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
