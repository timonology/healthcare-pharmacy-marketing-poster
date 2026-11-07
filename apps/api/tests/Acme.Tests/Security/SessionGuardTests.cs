using System.Reflection;
using Acme.Application.Auth;
using Acme.Domain.Users;
using Acme.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// The per-request session check (R2-09), and how it treats the two accounts
/// in a delegated token: the person who signed in, and the pharmacy worked as.
/// </summary>
public class SessionGuardTests
{
    private static readonly DateTime Issued = DateTime.UtcNow.AddSeconds(-5);

    private static CachedSessionGuard GuardFor(User user) =>
        new(UsersProxy.Holding(user), new MemoryCache(new MemoryCacheOptions()));

    private static User Active() =>
        User.Register(Email.Create($"a-{Guid.NewGuid():N}@example.test"), "Pharmacy", PasswordHash.FromHash("x"));

    private static User InvitedBranch() =>
        User.Invite(Email.Create($"b-{Guid.NewGuid():N}@example.test"), "Branch", PasswordHash.FromHash("x"));

    [Fact]
    public async Task An_invited_branch_can_be_worked_as_but_cannot_sign_in_itself()
    {
        // The bug the integration tests caught: the branch's own sign-in
        // rule was applied to a head office working as it.
        var branch = InvitedBranch();
        var guard = GuardFor(branch);

        (await guard.WhyEndedAsync(branch.Id, Issued, SessionParty.WorkedAs, default)).Should().BeNull();
        (await guard.WhyEndedAsync(branch.Id, Issued, SessionParty.SignedIn, default)).Should().NotBeNull(
            "an invited account still cannot hold a session of its own");
    }

    [Fact]
    public async Task A_deactivated_pharmacy_cannot_be_worked_as()
    {
        var branch = InvitedBranch();
        branch.Deactivate();

        (await GuardFor(branch).WhyEndedAsync(branch.Id, Issued, SessionParty.WorkedAs, default))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Reactivating_does_not_revive_a_token_issued_before_the_deactivation()
    {
        var pharmacy = Active();
        var issuedBefore = DateTime.UtcNow.AddSeconds(-5);

        pharmacy.Deactivate();
        pharmacy.Reactivate();

        var guard = GuardFor(pharmacy);
        (await guard.WhyEndedAsync(pharmacy.Id, issuedBefore, SessionParty.WorkedAs, default))
            .Should().NotBeNull("deactivation ended the session; reactivation must not restore it");
        (await guard.WhyEndedAsync(pharmacy.Id, issuedBefore, SessionParty.SignedIn, default))
            .Should().NotBeNull();
        (await guard.WhyEndedAsync(pharmacy.Id, DateTime.UtcNow.AddSeconds(5), SessionParty.WorkedAs, default))
            .Should().BeNull("a token issued after reactivation is fine");
    }

    [Fact]
    public async Task An_unknown_account_is_refused_in_either_part() =>
        (await new CachedSessionGuard(UsersProxy.Holding(null), new MemoryCache(new MemoryCacheOptions()))
            .WhyEndedAsync("nobody", Issued, SessionParty.WorkedAs, default))
            .Should().NotBeNull();

    /// <summary>An IUserRepository that answers FindByIdAsync and nothing else.</summary>
    public class UsersProxy : DispatchProxy
    {
        private User? _user;

        public static IUserRepository Holding(User? user)
        {
            var proxy = Create<IUserRepository, UsersProxy>();
            ((UsersProxy)(object)proxy)._user = user;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IUserRepository.FindByIdAsync))
                throw new NotSupportedException(targetMethod?.Name);

            User? found = _user is not null && (string?)args![0] == _user.Id ? _user : null;
            return Task.FromResult(found);
        }
    }
}
