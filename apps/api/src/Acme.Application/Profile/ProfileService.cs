using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Sonar;
using Acme.Domain.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;

namespace Acme.Application.Profile;

public sealed class ProfileService(
    IUserRepository users,
    ISonarApiClient sonar)
{
    public async Task<Result<MeDto>> GetMeAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        return user is null
            ? Result<MeDto>.NotFound("User not found.")
            : Result<MeDto>.Success(ToDto(user));
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
        return Result<MeDto>.Success(ToDto(user));
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
        return Result<MeDto>.Success(ToDto(user));
    }

    private static MeDto ToDto(User u) => new(
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
            u.Profile.OnboardingCompleted),
        u.CreatedAtUtc);
}
