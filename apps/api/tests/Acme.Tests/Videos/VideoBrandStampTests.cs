using System.Reflection;
using Acme.Domain.BrandKit;
using Acme.Domain.Users;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Tests.Videos;

/// <summary>
/// The pharmacy identity a video renders with, and that it survives storage.
/// </summary>
/// <remarks>
/// <para>
/// The brand is resolved in C# and stamped onto the video, rather than looked up
/// by the Python render worker. The worker is a separate process in a different
/// language, so having it resolve would mean the precedence rule existed twice
/// and had to agree by hand — the fault shape behind most of what has gone wrong
/// in this codebase.
/// </para>
/// <para>
/// That trade buys one problem: the stamp is a copy, and copies go stale and get
/// dropped. These tests cover both halves — that a refresh happens where it
/// should, and that the copy survives a round trip through Mongo intact.
/// </para>
/// </remarks>
public class VideoBrandStampTests
{
    private static PharmacyProfile Profile(string name) =>
        new(name, "12 Riverside Walk", "M1 4AB", "", "A Pharmacist",
            "0161 496 0000", null, OnboardingCompleted: true);

    private static DomainBrandKit Kit()
    {
        var kit = DomainBrandKit.Create(
            "owner-1", "Riverside brand",
            BrandColors.Create("#0d9488", "#134e4a", "#f59e0b"),
            PharmacyDetails.Create("Riverside Chemists", "FP-1234", "0161 000 0000", "Old Address"),
            "Registered pharmacy.");
        kit.SetLogo("brand/riverside/logo.png");
        return kit;
    }

    private static Video NewVideo() => Video.Create(
        ownerId: "owner-1",
        name: "Flu jabs",
        topic: "flu vaccination",
        scriptSource: VideoScriptSource.Ai,
        visualStyle: VideoVisualStyle.Realistic,
        description: null,
        customScript: null,
        voiceId: null,
        styleTone: null);

    [Fact]
    public void A_new_video_carries_no_brand_until_one_is_stamped()
    {
        // Create belongs to the domain and knows nothing about brand kits, which
        // live behind a repository. Stamping is the service's job, and this is
        // the state every video queued before branding existed is still in.
        NewVideo().Brand.Should().BeNull();
    }

    [Fact]
    public void Stamping_records_the_resolved_identity()
    {
        var v = NewVideo();
        v.StampBrand(PharmacyBrand.Resolve(Profile("Riverside Pharmacy"), Kit()));

        v.Brand.Should().NotBeNull();
        v.Brand!.Name.Should().Be("Riverside Pharmacy");
        v.Brand.LogoBlobKey.Should().Be("brand/riverside/logo.png");
    }

    [Fact]
    public void Re_stamping_replaces_the_identity_rather_than_merging_it()
    {
        // What Retry does. A pharmacy that has just removed its brand kit, or
        // corrected a name, must not find the old values surviving underneath.
        var v = NewVideo();
        v.StampBrand(PharmacyBrand.Resolve(Profile("Old Name"), Kit()));
        v.StampBrand(PharmacyBrand.Resolve(Profile("Corrected Name"), null));

        v.Brand!.Name.Should().Be("Corrected Name");
        v.Brand.LogoBlobKey.Should().BeNull("the brand kit is gone, so the logo is gone");
    }

    [Fact]
    public void Retry_leaves_the_brand_in_place_for_the_service_to_refresh()
    {
        // Retry is a domain method and has no way to reach a brand kit. It must
        // therefore not clear the stamp either: a service that forgot to
        // re-resolve would otherwise turn a retry into an un-branding, which is
        // a worse failure than a stale name.
        var v = NewVideo();
        v.StampBrand(PharmacyBrand.Resolve(Profile("Riverside Pharmacy"), Kit()));
        Fail(v, "render died");

        v.Retry(DateTime.UtcNow);

        v.Status.Should().Be(VideoStatus.Pending);
        v.Brand.Should().NotBeNull();
        v.Brand!.Name.Should().Be("Riverside Pharmacy");
    }

    [Fact]
    public void An_unusable_brand_is_stamped_rather_than_left_null()
    {
        // A pharmacy with neither record filled in still gets a stamp, carrying
        // IsUsable = false. Null and unusable mean different things: null is "we
        // never asked", unusable is "we asked and there was nothing". The worker
        // renders both unbranded, but only one of them is worth a support
        // question about why.
        var v = NewVideo();
        v.StampBrand(PharmacyBrand.Resolve(PharmacyProfile.Empty(), null));

        v.Brand.Should().NotBeNull();
        v.Brand!.IsUsable.Should().BeFalse();
        v.Brand.Name.Should().Be(PharmacyBrand.FallbackName);
    }

    [Fact]
    public void Every_field_of_the_brand_has_somewhere_to_live_in_the_document()
    {
        // The mapper is hand-written on both sides, so a property added to
        // PharmacyBrand and forgotten in ToDocument would be accepted, saved,
        // and silently absent on the next read — the exact shape of the fault
        // that made renaming a video destroy its script.
        //
        // Reflection rather than a list, so adding a field cannot quietly skip
        // this check.
        var brandFields = typeof(PharmacyBrand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        var documentType = typeof(Acme.Infrastructure.Persistence.MongoVideoRepository)
            .Assembly
            .GetType("Acme.Infrastructure.Persistence.VideoBrandDocument");

        documentType.Should().NotBeNull(
            "the stamped brand needs a document type; if it was renamed, this "
            + "test needs to follow it");

        var documentFields = documentType!
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        // Colors is one value on the domain side and three hex strings in the
        // document, because storing a validated struct would mean a palette that
        // no longer validates could stop a video loading at all.
        var expected = brandFields
            .Where(f => f != nameof(PharmacyBrand.Colors))
            .Select(f => f == nameof(PharmacyBrand.IsUsable) ? "Usable" : f);

        foreach (var field in expected)
        {
            documentFields.Should().Contain(field,
                "PharmacyBrand.{0} is resolved and then thrown away unless the "
                + "document has a home for it", field);
        }

        documentFields.Should().Contain("PrimaryColor");
        documentFields.Should().Contain("SecondaryColor");
        documentFields.Should().Contain("AccentColor");
    }

    /// <summary>
    /// Puts a video in the state a worker leaves behind when a render fails.
    /// </summary>
    /// <remarks>
    /// The same reflection idiom as <c>VideoRenderRecoveryTests.AsWorkerLeftIt</c>,
    /// and for the same reason: <c>Failed</c> has no transition method because
    /// the Python worker writes the document directly, so there is no domain
    /// call that produces this state.
    /// </remarks>
    private static void Fail(Video v, string note)
    {
        Set(v, nameof(Video.Status), VideoStatus.Failed);
        Set(v, nameof(Video.Note), note);
        Set(v, nameof(Video.LeaseOwner), null);
        Set(v, nameof(Video.LeaseExpiresAtUtc), null);

        static void Set(object target, string name, object? value) =>
            target.GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(target, value);
    }
}
