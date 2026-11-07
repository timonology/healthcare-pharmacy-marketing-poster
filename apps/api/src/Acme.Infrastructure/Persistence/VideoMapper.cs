using System.Reflection;
using Acme.Domain.BrandKit;
using Acme.Domain.Videos;

namespace Acme.Infrastructure.Persistence;

internal static class VideoMapper
{
    private static readonly ConstructorInfo Ctor = typeof(Video)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Video parameterless ctor missing.");

    public static Video Hydrate(VideoDocument d)
    {
        var v = (Video)Ctor.Invoke(null);
        Set(v, nameof(Video.Id), d.Id);
        Set(v, nameof(Video.OwnerId), d.OwnerId);
        Set(v, nameof(Video.Name), d.Name);
        Set(v, nameof(Video.Topic), d.Topic ?? d.Name);
        Set(v, nameof(Video.StyleTone), d.StyleTone);
        Set(v, nameof(Video.Source), (VideoSource)d.Source);
        Set(v, nameof(Video.Status), (VideoStatus)d.Status);
        Set(v, nameof(Video.ScriptSource), (VideoScriptSource)d.ScriptSource);
        Set(v, nameof(Video.VisualStyle), (VideoVisualStyle)d.VisualStyle);
        Set(v, nameof(Video.Description), d.Description);
        Set(v, nameof(Video.CustomScript), d.CustomScript);
        Set(v, nameof(Video.VoiceId), d.VoiceId);
        Set(v, nameof(Video.Images), HydrateImages(d.Images));
        Set(v, nameof(Video.Aspect), string.IsNullOrWhiteSpace(d.Aspect) ? "9:16" : d.Aspect);
        Set(v, nameof(Video.DurationSeconds), d.DurationSeconds == 0 ? 15 : d.DurationSeconds);
        Set(v, nameof(Video.BlobKey), d.BlobKey);
        Set(v, nameof(Video.ThumbnailBlobKey), d.ThumbnailBlobKey);
        Set(v, nameof(Video.Note), d.Note);
        Set(v, nameof(Video.Diagnostic), d.Diagnostic);
        Set(v, nameof(Video.LeaseOwner), d.LeaseOwner);
        Set(v, nameof(Video.LeaseExpiresAtUtc), d.LeaseExpiresAtUtc);
        Set(v, nameof(Video.Brand), HydrateBrand(d.Brand));
        Set(v, nameof(Video.Refine), HydrateRefine(d.Refine));
        Set(v, nameof(Video.CreatedAtUtc), d.CreatedAtUtc);
        Set(v, nameof(Video.UpdatedAtUtc), d.UpdatedAtUtc);
        return v;
    }

    public static VideoDocument ToDocument(Video v) => new()
    {
        Id       = v.Id,
        OwnerId  = v.OwnerId,
        Name     = v.Name,
        Topic    = v.Topic,
        StyleTone = v.StyleTone,
        Source   = (int)v.Source,
        Status   = (int)v.Status,
        ScriptSource = (int)v.ScriptSource,
        VisualStyle  = (int)v.VisualStyle,
        Description  = v.Description,
        CustomScript = v.CustomScript,
        VoiceId      = v.VoiceId,
        Images       = ToImageDocuments(v.Images),
        Aspect   = v.Aspect,
        DurationSeconds  = v.DurationSeconds,
        BlobKey          = v.BlobKey,
        ThumbnailBlobKey = v.ThumbnailBlobKey,
        Note             = v.Note,
        LeaseOwner        = v.LeaseOwner,
        LeaseExpiresAtUtc = v.LeaseExpiresAtUtc,
        Brand            = ToBrandDocument(v.Brand),
        Refine           = ToRefineDocument(v.Refine),
        CreatedAtUtc     = v.CreatedAtUtc,
        UpdatedAtUtc     = v.UpdatedAtUtc,
    };

    /// <summary>
    /// The pharmacy's own pictures, on the way out.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty list when there are none, so the field is
    /// absent from the document instead of stored as <c>[]</c> — see the
    /// <c>[BsonIgnoreIfNull]</c> note on <c>VideoDocument.Images</c>. Almost
    /// every video has none, and an empty array on every row is a field the
    /// worker then has to distinguish from a missing one for no reason.
    /// </remarks>
    private static List<VideoImageDocument>? ToImageDocuments(IReadOnlyList<VideoImage>? images)
    {
        if (images is null || images.Count == 0) return null;

        return images.Select(i => new VideoImageDocument
        {
            BlobKey = i.BlobKey,
            FocalX = i.FocalX,
            FocalY = i.FocalY,
            Zoom = i.Zoom,
            // Lowercase, matching the strings the Python worker compares
            // against. The same choice `refine.stage` makes, and for the same
            // reason: a name is wrong loudly, an ordinal is wrong quietly.
            Source = i.Source.ToString().ToLowerInvariant(),
            Description = i.Description,
        }).ToList();
    }

    /// <summary>
    /// Rebuilds the attached pictures, dropping any that cannot be read.
    /// </summary>
    /// <remarks>
    /// A row with a blank key or an unrecognised source is skipped rather than
    /// throwing. Hydration runs on every list and every poll, and one bad entry
    /// must not make a pharmacist's whole video unreadable — the render will
    /// simply use stock for that beat, which is what it does for an image that
    /// fails to download anyway.
    /// </remarks>
    private static IReadOnlyList<VideoImage> HydrateImages(List<VideoImageDocument>? docs)
    {
        if (docs is null || docs.Count == 0) return Array.Empty<VideoImage>();

        var images = new List<VideoImage>(docs.Count);
        foreach (var d in docs)
        {
            if (string.IsNullOrWhiteSpace(d.BlobKey)) continue;
            if (!Enum.TryParse<VideoImageSource>(d.Source, ignoreCase: true, out var source))
                source = VideoImageSource.Upload;

            images.Add(new VideoImage
            {
                BlobKey = d.BlobKey,
                Source = source,
                Description = d.Description,
                // Absent reads as centre, not as zero. Every picture attached
                // before the crop could be chosen has no stored point, and
                // `double`'s own default would silently reframe all of them to
                // the top-left corner.
                FocalX = d.FocalX ?? VideoImage.Centre,
                FocalY = d.FocalY ?? VideoImage.Centre,
                // Absent is 1.0 — every photo before zoom existed. Clamped on the
                // way in as well, since a hand-edited row is still a row.
                Zoom = VideoImage.ClampZoom(d.Zoom ?? VideoImage.ZoomDefault),
            });
        }
        return images;
    }

    private static VideoRefineDocument? ToRefineDocument(RefinePlan? plan) =>
        plan is null ? null : new VideoRefineDocument
        {
            Instruction = plan.Instruction,
            // Lowercase, matching the Python stage names. RefineStageContractTests
            // keeps the two lists in step; this is the one place the value
            // crosses between them.
            Stage = plan.Stage.ToString().ToLowerInvariant(),
            Summary = plan.Summary,
            CreatedAtUtc = plan.CreatedAtUtc,
            Confirmed = plan.Confirmed,
            PinsOwnPictures = plan.PinsOwnPictures,
            EditsShots = plan.EditsShots,
            AddedPictures = plan.AddedPictures,
            // Null rather than [] when there are none: an absent field is what
            // the worker reads as "not a picker refine".
            ShotEdits = plan.ChosenShots.Count == 0
                ? null
                : plan.ChosenShots
                    .Select(c => new ShotEditDocument { Index = c.Index, Show = c.Show })
                    .ToList(),
        };

    /// <summary>Rebuilds a stored plan, or null if it cannot be trusted.</summary>
    /// <remarks>
    /// An unreadable stage is the whole record discarded rather than a plan with
    /// a guessed stage. The stage decides what gets re-run and therefore what it
    /// costs; a video that comes back with no plan simply cannot be confirmed,
    /// and the pharmacist asks again.
    /// </remarks>
    private static RefinePlan? HydrateRefine(VideoRefineDocument? d)
    {
        if (d is null || string.IsNullOrWhiteSpace(d.Instruction)) return null;

        var stage = RefineStages.Parse(d.Stage);
        if (stage is null) return null;

        return new RefinePlan
        {
            Instruction = d.Instruction,
            Stage = stage.Value,
            Summary = d.Summary ?? string.Empty,
            CreatedAtUtc = d.CreatedAtUtc,
            Confirmed = d.Confirmed,
            PinsOwnPictures = d.PinsOwnPictures,
            EditsShots = d.EditsShots,
            AddedPictures = d.AddedPictures,
            ChosenShots = (d.ShotEdits ?? new List<ShotEditDocument>())
                .Select(e => ShotChoice.Restore(e.Index, e.Show))
                .ToList(),
        };
    }

    private static VideoBrandDocument? ToBrandDocument(PharmacyBrand? b) =>
        b is null ? null : new VideoBrandDocument
        {
            Name             = b.Name,
            Address          = b.Address,
            Phone            = b.Phone,
            BookingUrl       = b.BookingUrl,
            LogoBlobKey      = b.LogoBlobKey,
            PrimaryColor     = b.Colors?.Primary,
            SecondaryColor   = b.Colors?.Secondary,
            AccentColor      = b.Colors?.Accent,
            LicenseNumber    = b.LicenseNumber,
            RegulatoryFooter = b.RegulatoryFooter,
            Usable           = b.IsUsable,
        };

    /// <summary>Rebuilds the stamped brand, or null when a video has none.</summary>
    /// <remarks>
    /// <para>
    /// The colours come back through <see cref="BrandColors.Create"/> only when
    /// all three are present, because that factory rejects anything that is not
    /// a hex colour — and a stored value that no longer validates must not throw
    /// while loading a video. A pharmacy whose palette cannot be read still has
    /// a name and an address worth putting on screen.
    /// </para>
    /// <para>
    /// Any failure here degrades to no colours rather than no video. Hydration
    /// runs on every list and every poll; it is not a place to be strict.
    /// </para>
    /// </remarks>
    private static PharmacyBrand? HydrateBrand(VideoBrandDocument? d)
    {
        if (d is null) return null;

        BrandColors? colors = null;
        if (!string.IsNullOrWhiteSpace(d.PrimaryColor)
            && !string.IsNullOrWhiteSpace(d.SecondaryColor)
            && !string.IsNullOrWhiteSpace(d.AccentColor))
        {
            try
            {
                colors = BrandColors.Create(d.PrimaryColor, d.SecondaryColor, d.AccentColor);
            }
            catch (Acme.Domain.Common.DomainException)
            {
                colors = null;
            }
        }

        return new PharmacyBrand
        {
            Name             = string.IsNullOrWhiteSpace(d.Name) ? PharmacyBrand.FallbackName : d.Name,
            Address          = d.Address ?? string.Empty,
            Phone            = d.Phone ?? string.Empty,
            BookingUrl       = string.IsNullOrWhiteSpace(d.BookingUrl) ? null : d.BookingUrl,
            LogoBlobKey      = string.IsNullOrWhiteSpace(d.LogoBlobKey) ? null : d.LogoBlobKey,
            Colors           = colors,
            LicenseNumber    = d.LicenseNumber ?? string.Empty,
            RegulatoryFooter = d.RegulatoryFooter ?? string.Empty,
            IsUsable         = d.Usable,
        };
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
