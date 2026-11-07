using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Domain.Users;

namespace Acme.WebApi.Auditing;

/// <summary>
/// Who is acting, from the token: the real person, their kind, and the
/// pharmacy they are acting as.
/// </summary>
/// <remarks>
/// <para>
/// <b>The actor is the person, not the account.</b> A delegated token's
/// subject is the pharmacy being worked on and its <c>act</c> claim is who is
/// really driving. An administrator managing a shop is therefore recorded as
/// the administrator (kind <c>admin</c>), with the shop as <c>pharmacyId</c> —
/// staff work, reviewed as staff work. A head office working as its branch is
/// a pharmacy acting for a pharmacy (kind <c>pharmacy</c>).
/// </para>
/// <para>
/// The smoke-test account is a machine and is recorded as <c>system</c>, so a
/// deploy check does not fill the security view.
/// </para>
/// </remarks>
public static class AuditActorResolver
{
    public static AuditActor Resolve(ClaimsPrincipal user, string? smokeEmail)
    {
        if (user.Identity?.IsAuthenticated != true) return AuditActor.Anonymous;

        var subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject)) return AuditActor.Anonymous;

        var delegatedActor = user.FindFirstValue(DelegationClaims.Actor);
        if (!string.IsNullOrWhiteSpace(delegatedActor))
        {
            var actorEmail = user.FindFirstValue(DelegationClaims.ActorEmail);
            // Absent on tokens issued before act_role existed (an hour at most);
            // the middleware looks the actor up in that case.
            var actorRole = user.FindFirstValue(DelegationClaims.ActorRole);
            return new AuditActor(
                delegatedActor, actorEmail, actorRole,
                KindFor(actorRole, actorEmail, smokeEmail),
                PharmacyId: subject);
        }

        var email = user.FindFirstValue(JwtRegisteredClaimNames.Email)
                    ?? user.FindFirstValue(ClaimTypes.Email);
        var role = user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("role");
        var kind = KindFor(role, email, smokeEmail);

        // Staff working outside any pharmacy act for no tenant; a pharmacy
        // user's tenant is their own account.
        return new AuditActor(subject, email, role, kind,
            PharmacyId: kind == ActorKinds.Admin ? null : subject);
    }

    public static string KindFor(string? role, string? email, string? smokeEmail)
    {
        if (!string.IsNullOrWhiteSpace(smokeEmail) && !string.IsNullOrWhiteSpace(email)
            && string.Equals(email.Trim(), smokeEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            return ActorKinds.System;

        return role is nameof(UserRole.Admin) or nameof(UserRole.SuperAdmin)
            ? ActorKinds.Admin
            : ActorKinds.Pharmacy;
    }
}
