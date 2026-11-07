using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>One OAuth round trip, from Start to the pharmacy's choice of accounts.</summary>
/// <param name="OwnerId">Who started it. Every later step must be the same account.</param>
/// <param name="Kind">Which provider. A Google code is not redeemed against Meta.</param>
/// <param name="RedirectUri">
/// The one sent to the provider at Start. The code exchange must repeat it
/// exactly, so it is taken from here rather than from the callback request.
/// </param>
/// <param name="Accounts">What the provider offered, once the code is exchanged.</param>
public sealed record OAuthFlow(
    string OwnerId,
    SocialOAuthProviderKind Kind,
    string RedirectUri,
    IReadOnlyList<DiscoveredAccount>? Accounts = null);

/// <summary>
/// Server-side state for OAuth flows, keyed by the <c>state</c> parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes <c>state</c> mean something.</b> Start records the
/// flow against the signed-in pharmacy; the callback is refused unless the
/// state it brings back was issued by Start, to the same pharmacy, for the
/// same provider, and has not been used. Without it, a forged callback could
/// attach an attacker's Page to a victim's pharmacy (login CSRF).
/// </para>
/// <para>
/// Every step <i>takes</i> rather than reads — atomically, so a state or a set
/// of discovered accounts can be redeemed once and only once, even with
/// several API instances behind a load balancer.
/// </para>
/// <para>
/// Discovered accounts carry live platform tokens for accounts the pharmacy
/// has not yet chosen to connect. They are held encrypted, for minutes, and
/// deleted on use.
/// </para>
/// </remarks>
public interface IOAuthSessionStore
{
    /// <summary>Records a flow at Start.</summary>
    Task BeginAsync(string state, OAuthFlow flow, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Claims a started flow for its code exchange. Null unless the state
    /// exists, belongs to this owner and provider, is unexpired, and has not
    /// been claimed before.
    /// </summary>
    Task<OAuthFlow?> TakeStartedAsync(
        string state, string ownerId, SocialOAuthProviderKind kind, CancellationToken ct);

    /// <summary>Stores what the exchange found, for the pharmacy to choose from.</summary>
    Task SaveDiscoveredAsync(string state, OAuthFlow flow, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Removes and returns the discovered accounts. Null unless they exist,
    /// belong to this owner, and are unexpired.
    /// </summary>
    Task<OAuthFlow?> TakeDiscoveredAsync(string state, string ownerId, CancellationToken ct);
}

/// <summary>
/// Connecting, listing and disconnecting a pharmacy's external accounts.
/// </summary>
/// <remarks>
/// <para>
/// Every method is scoped by owner. A connection id is a bearer of authority
/// over someone's Facebook Page, so no method here trusts an id from the
/// request without checking it belongs to the caller.
/// </para>
/// <para>
/// Social publishing is gated above the Free tier. The check lives here rather
/// than in the controller because it also has to apply on the OAuth callback,
/// which the pharmacy can reach by URL without passing through the UI that
/// would have hidden the button.
/// </para>
/// </remarks>
public sealed class SocialConnectionService(
    ISocialConnectionRepository connections,
    IScheduledPostRepository posts,
    IUserRepository users,
    ITokenCipher cipher,
    IOAuthSessionStore sessions,
    IEnumerable<ISocialOAuthProvider> providers,
    // The configured public web address, which is where the callback page
    // lives. The only redirect URI this server will put in a consent URL.
    IAuthLinkBuilder links,
    ILogger<SocialConnectionService> log)
{
    /// <summary>
    /// How long the pharmacy has to choose an account after consenting.
    /// </summary>
    /// <remarks>
    /// Ten minutes is generous for a two-click choice and short enough that a
    /// live Page token is not sitting in memory for an afternoon because
    /// somebody wandered off mid-flow.
    /// </remarks>
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

    public async Task<Result<IReadOnlyList<SocialConnectionDto>>> ListAsync(
        string ownerId, CancellationToken ct)
    {
        var items = await connections.ListByOwnerAsync(ownerId, ct);
        return Result<IReadOnlyList<SocialConnectionDto>>.Success(
            items.Where(c => c.Status != ConnectionStatus.Revoked).Select(ToDto).ToList());
    }

    /// <summary>
    /// What this pharmacy can do here right now, so the page can explain
    /// instead of offering buttons that fail.
    /// </summary>
    public async Task<Result<SocialAvailabilityDto>> AvailabilityAsync(string ownerId, CancellationToken ct)
    {
        var planDenial = await GateAsync(ownerId, ct);

        var list = Enum.GetValues<SocialOAuthProviderKind>().Select(kind =>
        {
            var provider = providers.FirstOrDefault(p => p.Kind == kind);
            var reason =
                planDenial is not null ? planDenial :
                !cipher.IsConfigured ? NotSetUp :
                provider is null || !provider.IsConfigured
                    ? kind == SocialOAuthProviderKind.Google
                        ? "Google Ads is not available on this server yet."
                        : $"{kind} is not configured on this server."
                    : null;
            return new SocialProviderAvailabilityDto(kind, reason is null, reason);
        }).ToList();

        return Result<SocialAvailabilityDto>.Success(
            new SocialAvailabilityDto(planDenial is null, planDenial, list));
    }

    private const string NotSetUp =
        "Social accounts are not set up on this server yet. An administrator needs to add the credential encryption key.";

    /// <summary>
    /// The callback page for a provider, on the configured public web address.
    /// </summary>
    /// <remarks>
    /// Built here rather than accepted from the browser. An open redirect URI
    /// is only as safe as the provider's allow-list, and the code it carries
    /// is a credential; the server deciding where it goes means the provider
    /// settings are a second lock, not the only one.
    /// </remarks>
    public string CallbackUrlFor(SocialOAuthProviderKind kind) =>
        links.BuildAppLink($"/social/callback/{kind.ToString().ToLowerInvariant()}");

    /// <summary>Builds the consent URL for a provider and records the flow.</summary>
    /// <param name="requestedRedirectUri">
    /// What the browser expects, if it says. Must equal <see cref="CallbackUrlFor"/>;
    /// a mismatch is refused with the address this server uses, because a
    /// silently different redirect produces a provider error that is much
    /// harder to read.
    /// </param>
    public async Task<Result<OAuthStartDto>> StartAsync(
        string ownerId, SocialOAuthProviderKind kind, string? requestedRedirectUri, string state,
        CancellationToken ct)
    {
        if (await GateAsync(ownerId, ct) is { } denial)
            return Result<OAuthStartDto>.Forbidden(denial);

        // Before anyone is sent to Meta or Google: without the key the
        // accounts they choose could not be stored, and the failure would
        // arrive after they had granted access.
        if (!cipher.IsConfigured)
            return Result<OAuthStartDto>.Invalid(NotSetUp);

        var provider = providers.FirstOrDefault(p => p.Kind == kind);
        if (provider is null)
            return Result<OAuthStartDto>.Invalid($"No integration is registered for {kind}.");
        if (!provider.IsConfigured)
            return Result<OAuthStartDto>.Invalid(
                $"{kind} is not configured on this server. An administrator needs to set the app credentials.");

        var redirectUri = CallbackUrlFor(kind);
        if (!string.IsNullOrWhiteSpace(requestedRedirectUri)
            && !string.Equals(requestedRedirectUri.TrimEnd('/'), redirectUri, StringComparison.Ordinal))
        {
            log.LogWarning(
                "OAuth start for {Kind} asked for redirect {Requested}; this server uses {Configured}",
                kind, requestedRedirectUri, redirectUri);
            // Both addresses, so the person — and whoever they send a screenshot
            // to — can see the mismatch rather than guess at it. The browser's
            // own origin is not a secret; it is in their address bar.
            var configured = redirectUri[..redirectUri.IndexOf("/social/", StringComparison.Ordinal)];
            var opened = Uri.TryCreate(requestedRedirectUri, UriKind.Absolute, out var asked)
                ? asked.GetLeftPart(UriPartial.Authority)
                : "another address";
            return Result<OAuthStartDto>.Invalid(
                $"Connections must be started from {configured}, and this page is open at {opened}. " +
                "Open the app at the first address and try again — or, if that is the address people " +
                "should use, an administrator needs to set Public__WebBaseUrl to it.");
        }

        await sessions.BeginAsync(state, new OAuthFlow(ownerId, kind, redirectUri), SessionTtl, ct);

        return Result<OAuthStartDto>.Success(
            new OAuthStartDto(provider.BuildAuthorizationUrl(state, redirectUri), state));
    }

    /// <summary>
    /// Handles the OAuth callback: exchanges the code and lists what was found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state must be one Start issued to this pharmacy for this provider,
    /// and is consumed here — a replayed callback finds nothing.
    /// </para>
    /// <para>
    /// Nothing is persisted as a connection here. The pharmacy may have granted
    /// access to five Pages and want one; connecting all of them and asking
    /// forgiveness would mean storing credentials they never intended us to keep.
    /// </para>
    /// </remarks>
    public async Task<Result<IReadOnlyList<DiscoveredAccountDto>>> CompleteAsync(
        string ownerId,
        SocialOAuthProviderKind kind,
        string code,
        string state,
        CancellationToken ct)
    {
        if (await GateAsync(ownerId, ct) is { } denial)
            return Result<IReadOnlyList<DiscoveredAccountDto>>.Forbidden(denial);

        var provider = providers.FirstOrDefault(p => p.Kind == kind);
        if (provider is null || !provider.IsConfigured)
            return Result<IReadOnlyList<DiscoveredAccountDto>>.Invalid(
                $"{kind} is not configured on this server.");

        var flow = await sessions.TakeStartedAsync(state, ownerId, kind, ct);
        if (flow is null)
        {
            // Expired, already used, another account's, or never issued. One
            // answer for all four: telling them apart helps only someone probing.
            log.LogWarning("OAuth callback for {Kind} with an unknown or used state, owner {Owner}", kind, ownerId);
            return Result<IReadOnlyList<DiscoveredAccountDto>>.Invalid(
                "That connection attempt has expired or was already used. Please start again.");
        }

        IReadOnlyList<DiscoveredAccount> discovered;
        try
        {
            discovered = await provider.ExchangeCodeAsync(code, flow.RedirectUri, state, ct);
        }
        catch (Exception ex)
        {
            // The provider's message is surfaced because platform errors here
            // are usually actionable — a redirect URI that doesn't match the
            // app settings, or a scope that wasn't granted. Swallowing it in
            // favour of "something went wrong" turns a five-minute fix into an
            // afternoon.
            log.LogWarning(ex, "OAuth exchange failed for {Kind}, owner {Owner}", kind, ownerId);
            return Result<IReadOnlyList<DiscoveredAccountDto>>.Invalid(
                $"{kind} refused the connection: {ex.Message}");
        }

        if (discovered.Count == 0)
            return Result<IReadOnlyList<DiscoveredAccountDto>>.Invalid(
                kind == SocialOAuthProviderKind.Meta
                    ? "No Facebook Pages were found on that account. You need to be an admin of at least one Page, and an Instagram Business account must be linked to a Page before it appears here."
                    : "No usable accounts were found on that login.");

        await sessions.SaveDiscoveredAsync(state, flow with { Accounts = discovered }, SessionTtl, ct);

        var existing = await connections.ListByOwnerAsync(ownerId, ct);

        var dtos = discovered.Select((account, index) => new DiscoveredAccountDto(
            Handle: index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExternalAccountId: account.ExternalAccountId,
            Platform: account.Platform,
            PlatformName: account.Platform.DisplayName(),
            DisplayName: account.DisplayName,
            AvatarUrl: account.AvatarUrl,
            AlreadyConnected: existing.Any(c =>
                c.Platform == account.Platform
                && c.ExternalAccountId == account.ExternalAccountId
                && c.Status != ConnectionStatus.Revoked)))
            .ToList();

        return Result<IReadOnlyList<DiscoveredAccountDto>>.Success(dtos);
    }

    /// <summary>Persists the accounts the pharmacy chose.</summary>
    public async Task<Result<IReadOnlyList<SocialConnectionDto>>> ConnectAsync(
        string ownerId, ConnectAccountsRequest request, CancellationToken ct)
    {
        if (await GateAsync(ownerId, ct) is { } denial)
            return Result<IReadOnlyList<SocialConnectionDto>>.Forbidden(denial);

        if (string.IsNullOrWhiteSpace(request.SessionId))
            return Result<IReadOnlyList<SocialConnectionDto>>.Invalid(
                "That connection attempt has expired. Please start again.");

        // Taken, not read: single use, and only by the pharmacy that started
        // it — a handle leaked from one pharmacy's callback is useless to
        // another, and cannot be replayed by the same one.
        var session = await sessions.TakeDiscoveredAsync(request.SessionId, ownerId, ct);
        if (session?.Accounts is not { } accounts)
            return Result<IReadOnlyList<SocialConnectionDto>>.Invalid(
                "That connection attempt has expired. Please start again.");

        var chosen = new List<SocialConnection>();

        foreach (var handle in request.Handles.Distinct())
        {
            if (!int.TryParse(handle, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index)) continue;
            if (index < 0 || index >= accounts.Count) continue;

            var account = accounts[index];

            // The cipher refuses an empty credential, and rightly so. Caught
            // here rather than letting it surface as a 500: if a provider hands
            // back an account with no token, the pharmacy should be told that
            // account cannot be connected, not shown a server error.
            if (string.IsNullOrWhiteSpace(account.AccessToken))
            {
                log.LogWarning(
                    "Discovered account {Account} on {Platform} carried no access token; skipping",
                    account.ExternalAccountId, account.Platform);
                continue;
            }

            var accessToken = cipher.Encrypt(account.AccessToken);
            var refreshToken = string.IsNullOrWhiteSpace(account.RefreshToken)
                ? null
                : cipher.Encrypt(account.RefreshToken!);

            // Reconnecting an account we already hold updates it in place, so
            // scheduled posts pointing at that connection keep working.
            var existing = await connections.FindByExternalAsync(
                ownerId, account.Platform, account.ExternalAccountId, ct);

            if (existing is not null)
            {
                existing.UpdateCredentials(accessToken, refreshToken, account.ExpiresAtUtc, account.Scopes);
                existing.UpdateProfile(account.DisplayName, account.AvatarUrl);
                // An Instagram account can be moved to a different Page. Its
                // token is that Page's, so the link has to follow.
                existing.UpdateParent(account.ParentAccountId);
                existing.UpdateAuthorizer(account.AuthorizedByUserId);
                if (!string.IsNullOrWhiteSpace(account.RefreshToken))
                    existing.UpdateGrantFingerprint(Fingerprint(account.RefreshToken!));
                await connections.UpdateAsync(existing, ct);
                chosen.Add(existing);
            }
            else
            {
                var created = SocialConnection.Create(
                    ownerId,
                    account.Platform,
                    account.ExternalAccountId,
                    account.DisplayName,
                    accessToken,
                    refreshToken,
                    account.ExpiresAtUtc,
                    account.Scopes,
                    account.ParentAccountId,
                    account.AvatarUrl);
                created.UpdateAuthorizer(account.AuthorizedByUserId);
                if (!string.IsNullOrWhiteSpace(account.RefreshToken))
                    created.UpdateGrantFingerprint(Fingerprint(account.RefreshToken!));

                await connections.AddAsync(created, ct);
                chosen.Add(created);
            }
        }

        if (chosen.Count == 0)
            return Result<IReadOnlyList<SocialConnectionDto>>.Invalid("No accounts were selected.");

        log.LogInformation(
            "Pharmacy {Owner} connected {Count} account(s): {Accounts}",
            ownerId, chosen.Count, string.Join(", ", chosen.Select(c => $"{c.Platform}:{c.DisplayName}")));

        return Result<IReadOnlyList<SocialConnectionDto>>.Success(chosen.Select(ToDto).ToList());
    }

    /// <summary>Disconnects an account and destroys its stored credentials.</summary>
    public async Task<Result<bool>> DisconnectAsync(
        string ownerId, string connectionId, CancellationToken ct)
    {
        var connection = await connections.FindByIdAsync(connectionId, ct);
        if (connection is null || connection.OwnerId != ownerId)
            return Result<bool>.NotFound("Connection not found.");

        var pending = await posts.CountPendingForConnectionAsync(connectionId, ct);

        // Read before Revoke() discards it.
        var grant = await GrantToRevokeAsync(connection, ct);

        connection.Revoke();
        await connections.UpdateAsync(connection, ct);

        if (grant is { } g)
        {
            try
            {
                await g.Provider.RevokeAsync(g.Credential, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Our copy is already gone, which is what disconnecting
                // promises. A platform that cannot be reached right now does
                // not undo that.
                log.LogWarning(ex, "Could not revoke the grant at {Platform} for connection {Id}",
                    connection.Platform, connection.Id);
            }
        }

        log.LogInformation(
            "Pharmacy {Owner} disconnected {Platform} account {Account}",
            ownerId, connection.Platform, connection.DisplayName);

        // Reported rather than blocked. The pharmacy is entitled to revoke our
        // access immediately; the scheduled posts will fail with a clear reason
        // when their time comes, which is better than refusing to disconnect.
        if (pending > 0)
            log.LogWarning(
                "{Count} scheduled post(s) still target the disconnected connection {Id}",
                pending, connectionId);

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// The platform grant to withdraw when this connection goes, or null.
    /// </summary>
    /// <remarks>
    /// Only Google has a per-grant revocation worth calling, and one Google
    /// login can back several Ads connections — one per Ads account — that
    /// share a refresh token. Revoking while another still uses it would
    /// silently break that one, so the grant is only withdrawn with its last
    /// user — in any pharmacy.
    /// </remarks>
    private async Task<(ISocialOAuthProvider Provider, string Credential)?> GrantToRevokeAsync(
        SocialConnection connection, CancellationToken ct)
    {
        // TikTok and X: one sign-in, one account. Revoking may withdraw the
        // person's whole authorisation of the app, so it is skipped while the
        // same user is still connected elsewhere (another pharmacy).
        if (connection.Platform is SocialPlatform.TikTokAccount or SocialPlatform.XAccount)
        {
            var kind = connection.Platform == SocialPlatform.TikTokAccount
                ? SocialOAuthProviderKind.TikTok
                : SocialOAuthProviderKind.X;
            if (connection.AccessToken is null
                || providers.FirstOrDefault(p => p.Kind == kind) is not { } single)
                return null;
            try
            {
                var others = await connections.ListByAuthorizerAsync(
                    new[] { connection.Platform },
                    connection.AuthorizedByUserId ?? connection.ExternalAccountId, ct);
                if (others.Any(c => c.Id != connection.Id && c.Status != ConnectionStatus.Revoked)) return null;
                return (single, cipher.Decrypt(connection.AccessToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not read connection {Id}'s grant to revoke it", connection.Id);
                return null;
            }
        }

        // LinkedIn: one sign-in backs the profile and every Page connected
        // with it, all holding the same credential. Revoked only with its
        // last user, in any pharmacy — the same rule as Google's below.
        if (connection.Platform.IsLinkedIn())
        {
            if (connection.RefreshToken is null
                || providers.FirstOrDefault(p => p.Kind == SocialOAuthProviderKind.LinkedIn) is not { } linkedIn)
                return null;
            try
            {
                var credential = cipher.Decrypt(connection.RefreshToken);
                var fingerprint = connection.GrantFingerprint ?? Fingerprint(credential);
                if (await connections.GrantInUseAsync(fingerprint, connection.Id, ct)) return null;

                // A reconnect can leave the same person's other LinkedIn
                // connections on a newer token. LinkedIn does not say whether
                // revoking one token ends the person's whole authorisation, so
                // while any of theirs is still connected — in any pharmacy —
                // the old token is left to expire on its own.
                if (!string.IsNullOrWhiteSpace(connection.AuthorizedByUserId))
                {
                    var theirs = await connections.ListByAuthorizerAsync(
                        new[] { SocialPlatform.LinkedInProfile, SocialPlatform.LinkedInPage },
                        connection.AuthorizedByUserId!, ct);
                    if (theirs.Any(c => c.Id != connection.Id && c.Status != ConnectionStatus.Revoked)) return null;
                }

                return (linkedIn, credential);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Could not read connection {Id}'s grant to revoke it", connection.Id);
                return null;
            }
        }

        if (connection.Platform is not (SocialPlatform.GoogleAds or SocialPlatform.GoogleBusinessProfile)
            || connection.RefreshToken is null
            || providers.FirstOrDefault(p => p.Kind == SocialOAuthProviderKind.Google) is not { } google)
            return null;

        // Google withdraws a person's whole sign-in to this app at once, so
        // revoking the Ads grant would also cut off a YouTube channel the same
        // pharmacy connected with the same Google account. We cannot tell from
        // here whether it is the same account, so with any YouTube channel
        // connected we leave the grant alone (our copy is still deleted).
        var owned = await connections.ListByOwnerAsync(connection.OwnerId, ct);
        if (owned.Any(c => c.Platform == SocialPlatform.YouTubeChannel && c.Status != ConnectionStatus.Revoked))
            return null;

        try
        {
            var token = cipher.Decrypt(connection.RefreshToken);
            var fingerprint = connection.GrantFingerprint ?? Fingerprint(token);

            // Across every pharmacy, not just this one: a head office may have
            // connected a branch with the same Google sign-in.
            return await connections.GrantInUseAsync(fingerprint, connection.Id, ct)
                ? null
                : (google, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read connection {Id}'s grant to revoke it", connection.Id);
            return null;
        }
    }

    /// <summary>SHA-256 of a credential, hex. Identifies a grant without revealing it.</summary>
    public static string Fingerprint(string credential) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(credential))).ToLowerInvariant();

    /// <summary>
    /// Free-tier accounts cannot publish. Returns the refusal text, or null.
    /// </summary>
    /// <remarks>
    /// Reads the tier directly rather than going through <c>Plans</c>, because
    /// <c>PlanDefinition</c> has no social flag yet. If more social limits
    /// appear — a cap on connected accounts, say — this should move there so
    /// there is one description of what a plan includes.
    /// </remarks>
    private async Task<string?> GateAsync(string ownerId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return "Account not found.";

        return user.Tier == SubscriptionTier.Free
            ? "Social publishing is available on the Starter plan and above."
            : null;
    }

    internal static SocialConnectionDto ToDto(SocialConnection c) => new(
        c.Id,
        c.Platform,
        c.Platform.DisplayName(),
        c.ExternalAccountId,
        c.DisplayName,
        c.AvatarUrl,
        c.Status,
        c.ExpiresAtUtc,
        c.LastVerifiedAtUtc,
        c.LastError,
        CanPublish: c.Platform.IsPublishTarget() && c.IsUsable(DateTime.UtcNow),
        SupportsVideo: c.Platform.SupportsVideo(),
        c.CreatedAtUtc,
        SupportsImages: c.Platform.SupportsImages());
}
