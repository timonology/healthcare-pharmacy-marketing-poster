using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Sonar;
using Acme.Domain.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;

namespace Acme.Application.Profile;

public sealed class ProfileService(
    IUserRepository users,
    IBrandKitRepository brandKits,
    ISonarApiClient sonar)
{
    public async Task<Result<MeDto>> GetMeAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        return user is null
            ? Result<MeDto>.NotFound("User not found.")
            : Result<MeDto>.Success(await ToDtoAsync(user, ct));
    }

    public async Task<Result<MeDto>> UpdateProfileAsync(
        string userId,
        UpsertProfileRequest request,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<MeDto>.NotFound("User not found.");

        try
        {
            // Caller may override the F code; fall back to whatever the user
            // already had on file.
            var fcode = !string.IsNullOrWhiteSpace(request.SonarFCode)
                ? request.SonarFCode
                : user.Profile.SonarFCode;

            var profile = PharmacyProfile.Create(
                request.PharmacyName,
                request.Address,
                request.PostCode,
                request.Description,
                request.ContactName,
                request.ContactPhone,
                fcode);

            user.SetProfile(profile);
        }
        catch (DomainException ex)
        {
            return Result<MeDto>.Invalid(ex.Message);
        }

        await users.UpdateAsync(user, ct);
        return Result<MeDto>.Success(await ToDtoAsync(user, ct));
    }

    public async Task<Result<MeDto>> OnboardAsync(
        string userId,
        OnboardRequest request,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<MeDto>.NotFound("User not found.");

        // If the user supplied an F/ODS code, trust it. Otherwise try the
        // Sonar lookup as a best-effort enrichment.
        string? fcode = request.SonarFCode;
        if (string.IsNullOrWhiteSpace(fcode))
        {
            var lookup = await sonar.LookupAsync(request.PharmacyName, request.Address, ct);
            fcode = lookup?.FCode;
        }

        try
        {
            var profile = PharmacyProfile.Create(
                request.PharmacyName,
                request.Address,
                request.PostCode,
                request.Description,
                request.ContactName,
                request.ContactPhone,
                fcode);

            user.SetProfile(profile);

            if (request.Tier is { } tier && tier != SubscriptionTier.Free)
                user.ChangeTier(tier);
        }
        catch (DomainException ex)
        {
            return Result<MeDto>.Invalid(ex.Message);
        }

        await users.UpdateAsync(user, ct);
        return Result<MeDto>.Success(await ToDtoAsync(user, ct));
    }

    /// <summary>
    /// The profile as the pharmacist sees it, including the booking link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The extra read is deliberate.</b> The booking link is stored on the
    /// brand kit and shown on the profile, so composing this DTO costs one more
    /// indexed lookup by owner. That is the price of there being exactly one
    /// copy of the URL, and it is the right trade: the alternative is a second
    /// stored value that agrees with the first until somebody edits one of them.
    /// </para>
    /// <para>
    /// A pharmacy with no brand kit gets null, which is correct — no kit means
    /// no link, and the profile page says where to set one.
    /// </para>
    /// </remarks>
    private async Task<MeDto> ToDtoAsync(User u, CancellationToken ct)
    {
        var kit = await brandKits.FindByOwnerAsync(u.Id, ct);

        return new MeDto(
            u.Id,
            u.Email.Value,
            u.DisplayName,
            u.Tier,
            new PharmacyProfileDto(
                u.Profile.PharmacyName,
                u.Profile.Address,
                u.Profile.PostCode,
                u.Profile.Description,
                u.Profile.ContactName,
                u.Profile.ContactPhone,
                u.Profile.SonarFCode,
                u.Profile.OnboardingCompleted,
                kit?.Pharmacy.BookingUrl),
            u.CreatedAtUtc,
            u.Role);
    }
}
