using System.Reflection;
using Acme.Domain.Templates;

namespace Acme.Infrastructure.Persistence;

internal static class TemplateMapper
{
    private static readonly ConstructorInfo Ctor = typeof(Template)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Template parameterless ctor missing.");

    public static Template Hydrate(TemplateDocument d)
    {
        var t = (Template)Ctor.Invoke(null);
        Set(t, nameof(Template.Id), d.Id);
        Set(t, nameof(Template.Name), d.Name);
        Set(t, nameof(Template.Description), d.Description);
        Set(t, nameof(Template.Category), (TemplateCategory)d.Category);
        Set(t, nameof(Template.Tags), (IReadOnlyList<string>)d.Tags.ToList());
        Set(t, nameof(Template.ThumbnailBlobKey), d.ThumbnailBlobKey);
        Set(t, nameof(Template.CanvasJson), CanvasJsonConverter.FromBson(d.Canvas));
        Set(t, nameof(Template.IsPublished), d.IsPublished);
        Set(t, nameof(Template.CreatedAtUtc), d.CreatedAtUtc);
        Set(t, nameof(Template.UpdatedAtUtc), d.UpdatedAtUtc);
        return t;
    }

    public static TemplateDocument ToDocument(Template t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Description = t.Description,
        Category = (int)t.Category,
        Tags = t.Tags.ToList(),
        ThumbnailBlobKey = t.ThumbnailBlobKey,
        Canvas = CanvasJsonConverter.ToBson(t.CanvasJson),
        IsPublished = t.IsPublished,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc,
    };

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
