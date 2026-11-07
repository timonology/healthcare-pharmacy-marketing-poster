using Acme.Application.Common;
using Acme.Domain.BrandKit;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Moves each pharmacy's booking link from its profile onto its brand kit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it moved.</b> The link was kept beside the address and the phone
/// number, as a contact detail — sound reasoning, wrong place. It is what a QR
/// code on a poster encodes, posters are laid out from the brand kit, and a
/// value a designer needs that lives in the form an owner fills in once during
/// onboarding is a value nobody can find. There is now exactly one copy and the
/// brand kit holds it.
/// </para>
/// <para>
/// <b>Why a brand kit gets created.</b> A kit only ever existed if the pharmacy
/// had opened the Brand Kit page and saved it, so a pharmacy could easily have a
/// booking link and no kit for it to move into. Leaving those behind would have
/// meant either losing their link or reading from two places for a while, and
/// reading from two places is the fault this change exists to remove. So a kit
/// is built from what the profile already knows. The registration number is left
/// blank — which is why <see cref="PharmacyDetails"/> stopped requiring one, and
/// is a far better outcome than inventing a number for a regulated field.
/// </para>
/// <para>
/// <b>What it will not do.</b> It never overwrites a booking link already on a
/// kit. If both exist, the kit's is the one somebody set deliberately in the
/// place that now owns the value, and the profile's is the stale copy.
/// </para>
/// <para>
/// Idempotent. The profile field is unset in the same pass, so a second run
/// matches nothing. Runs on every boot rather than behind a version flag, so an
/// environment restored from an old backup repairs itself — the same reasoning
/// as <see cref="UserFieldBackfill"/>.
/// </para>
/// </remarks>
public sealed class BookingLinkToBrandKitMigration(
    MongoContext ctx,
    ILogger<BookingLinkToBrandKitMigration> log) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var users = ctx.Collection<UserDocument>("users");
        var kits = ctx.Collection<BrandKitDocument>(MongoBrandKitRepository.CollectionName);

        var f = Builders<UserDocument>.Filter;

        // Both halves matter. `Exists` alone matches rows where somebody cleared
        // the link to null, and those have nothing to move — they would each
        // cost a brand-kit lookup, on every boot, forever.
        var carrying = f.And(
            f.Exists("profile.bookingUrl", true),
            f.Ne("profile.bookingUrl", (string?)null));

        var pending = await users.Find(carrying).ToListAsync(ct);
        if (pending.Count == 0) return;

        int moved = 0, created = 0, dropped = 0, alreadySet = 0, unresolved = 0;

        foreach (var user in pending)
        {
            ct.ThrowIfCancellationRequested();

            var raw = user.Profile?.BookingUrl;

            // Normalised on the way through, without throwing. These values were
            // stored before the current gate existed in this form, and one bad
            // row must not abort the pass and strand every pharmacy after it.
            var link = BookingLink.TryNormalize(raw);
            if (link is null && !string.IsNullOrWhiteSpace(raw))
            {
                log.LogWarning(
                    "Dropping an unusable booking link on user {UserId} during the "
                    + "move to brand kits. It would not have rendered either — an "
                    + "unencodable link is already skipped by every renderer.",
                    user.Id);
                dropped++;
            }

            if (link is not null)
            {
                var kit = await kits
                    .Find(k => k.OwnerId == user.Id)
                    .FirstOrDefaultAsync(ct);

                if (kit is null)
                {
                    try
                    {
                        await kits.InsertOneAsync(BuildKitFrom(user, link), cancellationToken: ct);
                        created++;
                    }
                    catch (MongoWriteException ex)
                        when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
                    {
                        // Another instance created this pharmacy's kit between
                        // the read above and this write.
                        //
                        // <b>Worth catching rather than letting fly.</b> Startup
                        // tasks run before the web host starts serving and a
                        // throw here returns exit code 1 — so on a rolling
                        // deploy, two replicas booting together would race,
                        // one would lose, and the loser would fail to start.
                        // The restart would succeed, because by then the kit
                        // exists; the incident would still be real.
                        //
                        // `uniq_owner` on brand_kits is what makes this
                        // detectable at all, and it is the reason the loser can
                        // simply fall through to the update path below.
                        kit = await kits
                            .Find(k => k.OwnerId == user.Id)
                            .FirstOrDefaultAsync(ct);
                        log.LogInformation(
                            "Brand kit for user {UserId} was created concurrently; "
                            + "moving the booking link onto it instead.", user.Id);
                    }
                }

                if (kit is null)
                {
                    // Lost the insert race and then could not read the winner
                    // back — a lagging secondary, or the other instance's insert
                    // did not survive. Vanishingly rare, and the cost of getting
                    // it wrong is a pharmacy's booking link, so the source value
                    // stays put and the next boot tries again.
                    log.LogError(
                        "Could not place the booking link for user {UserId} on a "
                        + "brand kit. Leaving it on the profile for the next boot "
                        + "rather than clearing it.", user.Id);
                    unresolved++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(kit.Pharmacy.BookingUrl))
                {
                    // The id, not the document. A lambda capturing a nullable
                    // local is analysed conservatively however the surrounding
                    // flow narrows it, and this assembly builds with warnings as
                    // errors — so capturing `kit` here is a build failure rather
                    // than a squiggle.
                    var kitId = kit.Id;

                    await kits.UpdateOneAsync(
                        k => k.Id == kitId,
                        Builders<BrandKitDocument>.Update
                            .Set("pharmacy.bookingUrl", link)
                            .Set(k => k.UpdatedAtUtc, DateTime.UtcNow),
                        cancellationToken: ct);
                    moved++;
                }
                else
                {
                    alreadySet++;
                }
            }

            // Unset last, and only once this value's fate is settled. Last, so a
            // failure anywhere above leaves the source in place for the next boot
            // to retry rather than losing it. Every path that reaches here has
            // decided what happens to the link — kept on a kit, superseded by one
            // already there, or unusable — and leaving it behind after that would
            // be the second copy this whole change exists to abolish. The one
            // undecided path `continue`s above rather than falling through here,
            // which is the distinction an unconditional unset got wrong.
            await users.UpdateOneAsync(
                u => u.Id == user.Id,
                Builders<UserDocument>.Update.Unset("profile.bookingUrl"),
                cancellationToken: ct);
        }

        log.LogInformation(
            "Booking links moved to brand kits: {Moved} onto an existing kit, "
            + "{Created} onto a kit created for the purpose, {AlreadySet} left alone "
            + "because the kit already had one, {Dropped} unusable, {Unresolved} "
            + "left on the profile for the next boot. {Examined} pharmac(ies) examined.",
            moved, created, alreadySet, dropped, unresolved, pending.Count);
    }

    /// <summary>A minimal kit carrying what the profile already knew.</summary>
    /// <remarks>
    /// Built as a document rather than through <see cref="DomainBrandKit.Create"/>
    /// because the domain requires a pharmacy name and some of these profiles have
    /// none — an account that skipped onboarding but set a booking link. Refusing
    /// those would leave exactly the pharmacies with the least filled in as the
    /// ones that lose data.
    /// </remarks>
    private static BrandKitDocument BuildKitFrom(UserDocument user, string link)
    {
        var now = DateTime.UtcNow;

        // Through a local, so the null-state narrowing is on a plain string? and
        // not on a member access behind a conditional. `IsNullOrWhiteSpace` is
        // annotated `[NotNullWhen(false)]`, which makes this form unambiguous —
        // and unambiguous matters here, because a nullable warning in this
        // assembly is a build failure.
        var rawName = user.Profile?.PharmacyName;
        var name = string.IsNullOrWhiteSpace(rawName)
            ? PharmacyBrand.FallbackName
            : rawName.Trim();

        var address = string.Join(", ", new[] { user.Profile?.Address, user.Profile?.PostCode }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim()));

        return new BrandKitDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = user.Id,
            Name = name,
            Colors = new BrandColorsDocument(),
            Pharmacy = new PharmacyDetailsDocument
            {
                Name = name,
                // Blank on purpose. See the remarks above: there is no honest
                // value to put here and a regulated field is not a thing to guess.
                LicenseNumber = string.Empty,
                Phone = user.Profile?.ContactPhone?.Trim() ?? string.Empty,
                Address = address,
                BookingUrl = link,
            },
            RegulatoryFooter = string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }
}
