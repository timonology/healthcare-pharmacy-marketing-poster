using Acme.Application.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Writes default values into user documents that predate the fields.
/// </summary>
/// <remarks>
/// <para>
/// <c>role</c>, <c>tier</c> and <c>isActive</c> were added after accounts
/// already existed. Each is marked <c>[BsonDefaultValue]</c>, which fixes
/// DESERIALISATION — an old document reads back as an active Free pharmacy
/// user. It does nothing for QUERIES, because those run in the database, and in
/// MongoDB a document that lacks a field does not match an equality test
/// against that field's default.
/// </para>
/// <para>
/// The effect was silent and specific: the users table listed every pharmacy
/// account correctly, but filtering by role returned only accounts created
/// after roles shipped. The Pharmacies page, which always filters by role, and
/// the dashboard's pharmacy count were wrong for the same reason — while the
/// unfiltered total was right, so the numbers disagreed with each other.
/// </para>
/// <para>
/// Idempotent and cheap: each filter matches only the documents that still need
/// the repair, so after the first run every update matches nothing. Runs on
/// every boot rather than behind a version flag so an environment restored from
/// an old backup repairs itself.
/// </para>
/// </remarks>
public sealed class UserFieldBackfill(
    MongoContext ctx,
    ILogger<UserFieldBackfill> log) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var users = ctx.Collection<UserDocument>("users");
        var f = Builders<UserDocument>.Filter;
        var u = Builders<UserDocument>.Update;

        var repairs = new (string Field, FilterDefinition<UserDocument> Missing, UpdateDefinition<UserDocument> Set)[]
        {
            ("role",
             f.Exists(x => x.Role, false),
             u.Set(x => x.Role, (int)UserRole.PharmacyUser)),

            ("tier",
             f.Exists(x => x.Tier, false),
             u.Set(x => x.Tier, (int)SubscriptionTier.Free)),

            // Absent means "was never deactivated", so the safe default is
            // active. Defaulting to false would lock out every legacy account.
            ("isActive",
             f.Exists(x => x.IsActive, false),
             u.Set(x => x.IsActive, true)),

            // Staff have no pharmacy to set up, and an unfinished flag holds
            // them at the onboarding form — which the middleware enforces on
            // every protected path, the admin console included.
            //
            // Not a missing-field repair like the three above: the field is
            // there and says false. It is here because it has the same shape —
            // a fact that was true when the row was written and is not true
            // now — and because the alternative was a startup task of its own
            // for one UpdateMany. New staff accounts come out right at source;
            // see PharmacyProfile.ForStaff.
            // `Ne(..., true)` rather than `Eq(..., false)`: in MongoDB a
            // document with no `profile` sub-document does not match an
            // equality test against false, and those are exactly the oldest
            // rows — the ones most likely to be stuck.
            ("profile.onboardingCompleted (staff)",
             f.And(
                 f.Ne(x => x.Role, (int)UserRole.PharmacyUser),
                 f.Ne("profile.onboardingCompleted", true)),
             u.Set("profile.onboardingCompleted", true)),
        };

        foreach (var (field, missing, set) in repairs)
        {
            var result = await users.UpdateManyAsync(missing, set, cancellationToken: ct);
            if (result.ModifiedCount > 0)
            {
                log.LogInformation(
                    "Backfilled '{Field}' on {Count} user document(s) that predated it.",
                    field, result.ModifiedCount);
            }
        }
    }
}
