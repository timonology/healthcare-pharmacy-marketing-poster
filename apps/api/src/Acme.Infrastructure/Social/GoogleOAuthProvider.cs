using System.Text.Json;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Google's OAuth flow, producing Google Ads connections.
/// </summary>
/// <remarks>
/// <para>
/// Google Ads authenticates with a <b>refresh</b> token, exchanged for a short
/// access token on every call — unlike Meta, where the stored Page token is
/// used directly. So the refresh token is the credential that matters here, and
/// a connection without one cannot work at all.
/// </para>
/// <para>
/// Google only issues a refresh token on the <i>first</i> consent for a given
/// user and client, and silently omits it afterwards. Hence
/// <c>access_type=offline</c> together with <c>prompt=consent</c>: without the
/// forced prompt, a pharmacy reconnecting after revoking access gets a grant
/// with no refresh token, and the connection looks healthy until the first API
/// call fails. <see cref="SocialConnection.UpdateCredentials"/> also refuses to
/// overwrite an existing refresh token with null, for the same reason.
/// </para>
/// </remarks>
public sealed class GoogleOAuthProvider(
    IHttpClientFactory httpFactory,
    IOptions<GoogleOptions> options,
    IGoogleAdsClient adsClient,
    ILogger<GoogleOAuthProvider> log) : ISocialOAuthProvider
{
    private readonly GoogleOptions _options = options.Value;

    public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.Google;
    /// <summary>
    /// The OAuth client <i>and</i> a working Ads client.
    /// </summary>
    /// <remarks>
    /// Connecting Google produces Google Ads connections and nothing else, so
    /// without the Ads client there is nothing to connect. Saying so before
    /// the pharmacy is sent to Google's consent screen beats letting them
    /// grant access and then telling them it was for nothing.
    /// </remarks>
    public bool IsConfigured => _options.IsConfigured && adsClient.IsConfigured;

    /// <summary>
    /// Scopes requested at consent.
    /// </summary>
    /// <remarks>
    /// <c>adwords</c> covers the whole Google Ads API — it is not divisible, so
    /// there is no narrower option.
    /// <para>
    /// Business Profile (<c>business.manage</c>) is deliberately <b>not</b>
    /// requested until GBP publishing exists. It is a sensitive scope: asking
    /// for it puts the app through a heavier Google verification, and asking
    /// for access we do not use is exactly what that review rejects. When GBP
    /// is built, add it here; <c>include_granted_scopes</c> lets a pharmacy
    /// grant it incrementally without losing the Ads grant.
    /// </para>
    /// </remarks>
    private static readonly string[] Scopes =
    {
        "https://www.googleapis.com/auth/adwords",
    };

    public string BuildAuthorizationUrl(string state, string redirectUri)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = string.Join(" ", Scopes),
            ["response_type"] = "code",
            // Both are required to reliably receive a refresh token.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
        };

        return "https://accounts.google.com/o/oauth2/v2/auth" + BuildQuery(query);
    }

    public async Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.Google);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });

        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        using var json = await ReadJsonAsync(response, ct);

        var accessToken = GetString(json, "access_token");
        var refreshToken = GetString(json, "refresh_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Google did not return an access token.");

        // Stated plainly rather than stored and left to fail later. Without a
        // refresh token every Ads call will fail, and the pharmacy would have
        // no idea why a connection showing as healthy does nothing.
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                "Google did not return a refresh token. Remove this app's access at " +
                "myaccount.google.com/permissions and connect again.");

        // One connection per Ads account the login can reach, so a pharmacy
        // group managing several picks the right one rather than us guessing.
        if (adsClient.IsConfigured)
        {
            try
            {
                var customers = await adsClient.ListAccessibleCustomersAsync(refreshToken!, ct);

                if (customers.Count > 0)
                {
                    return customers.Select(c => new DiscoveredAccount(
                        c.CustomerId,
                        string.IsNullOrWhiteSpace(c.DescriptiveName)
                            ? $"Google Ads {c.CustomerId}"
                            : c.DescriptiveName,
                        SocialPlatform.GoogleAds,
                        accessToken!,
                        refreshToken,
                        // No expiry on the connection: the refresh token is the
                        // credential and does not lapse; the hour-long access
                        // token is fetched fresh per call. Recording its expiry
                        // would mark a working connection unusable after an hour.
                        ExpiresAtUtc: null,
                        Scopes,
                        // Reached through a manager account: every call for
                        // this account must name it as login-customer-id.
                        ParentAccountId: c.LoginCustomerId)).ToList();
                }

                log.LogWarning("Google returned no accessible Ads customers for this login");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The consent succeeded; only the account listing failed. Worth
                // saying so, rather than discarding a valid grant — but not
                // worth inventing a connection we cannot name.
                log.LogWarning(ex, "Could not list Google Ads customers after consent");
                throw new InvalidOperationException(
                    $"Signed in to Google, but could not list your Ads accounts: {ex.Message}");
            }

            // Configured, signed in, and simply no account to run ads in.
            throw new InvalidOperationException(
                "This Google sign-in has no Google Ads account it can run campaigns in. " +
                "Sign in with the Google account that manages your ads, or create one at ads.google.com.");
        }

        throw new InvalidOperationException(
            "Google Ads is not configured on this server, so there is nothing to connect yet. " +
            "An administrator needs to set the Google OAuth client (Social__Google__ClientId / ClientSecret).");
    }

    /// <summary>
    /// Revokes the refresh token at Google, which ends the whole grant for
    /// this login. The caller only does that when no other connection still
    /// uses the same grant.
    /// </summary>
    public async Task RevokeAsync(string credential, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.Google);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = credential });
        using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", form, ct);

        // 400 "invalid_token" means it was already revoked — the outcome we wanted.
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 400)
            log.LogWarning("Google token revocation answered {Status}", (int)response.StatusCode);
    }

    /* ---------------- helpers ---------------- */

    private static string BuildQuery(Dictionary<string, string?> query) =>
        "?" + string.Join("&", query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

    private static string? GetString(JsonDocument json, string field) =>
        json.RootElement.TryGetProperty(field, out var value) ? value.GetString() : null;

    /// <summary>
    /// Parses a token response, surfacing Google's own error description.
    /// </summary>
    /// <remarks>
    /// Google's <c>error_description</c> is unusually specific — it names a
    /// redirect URI mismatch as exactly that. Replacing it with a generic
    /// message throws away the one thing that fixes the problem.
    /// </remarks>
    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Google returned a non-JSON response ({(int)response.StatusCode}).");
        }

        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var description = json.RootElement.TryGetProperty("error_description", out var d)
                ? d.GetString()
                : error.GetString();

            json.Dispose();
            throw new InvalidOperationException(description ?? "Google rejected the request.");
        }

        if (!response.IsSuccessStatusCode)
        {
            json.Dispose();
            throw new InvalidOperationException($"Google returned {(int)response.StatusCode}.");
        }

        return json;
    }
}
