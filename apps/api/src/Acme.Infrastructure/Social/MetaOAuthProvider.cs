using System.Net.Http.Json;
using System.Text.Json;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Meta's OAuth flow, producing Facebook Page and Instagram Business connections.
/// </summary>
/// <remarks>
/// <para>
/// One consent yields both platforms, which is why this is a single provider
/// rather than two. Meta issues no standalone Instagram credential: an
/// Instagram Business account is reached through the Page it is linked to,
/// using that Page's token. So each Instagram account discovered here carries
/// its Page id as <c>ParentAccountId</c>.
/// </para>
/// <para>
/// The token stored is the <b>Page</b> token, exchanged from a long-lived user
/// token. Page tokens derived that way do not carry their own expiry, which is
/// why connections may have a null <c>ExpiresAtUtc</c> — not an oversight, and
/// not a reason to treat them as permanently valid either, since the user can
/// revoke at any time.
/// </para>
/// </remarks>
public sealed class MetaOAuthProvider(
    IHttpClientFactory httpFactory,
    IOptions<MetaOptions> options,
    ILogger<MetaOAuthProvider> log) : ISocialOAuthProvider
{
    private readonly MetaOptions _options = options.Value;

    public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.Meta;
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Scopes requested at consent.
    /// </summary>
    /// <remarks>
    /// Each one requires App Review before it works for anyone outside the
    /// app's own team, and each is here for a specific call:
    ///   pages_show_list          — enumerate the Pages the user administers
    ///   pages_read_engagement    — read Page name and picture for the card
    ///   pages_manage_posts       — create posts on the Page
    ///   instagram_basic          — resolve the linked Instagram account
    ///   instagram_content_publish— create and publish IG media
    /// business_management is deliberately not requested: it grants far more
    /// than publishing needs and makes App Review harder to pass.
    /// </remarks>
    private static readonly string[] Scopes =
    {
        "pages_show_list",
        "pages_read_engagement",
        "pages_manage_posts",
        "instagram_basic",
        "instagram_content_publish",
    };

    public string BuildAuthorizationUrl(string state, string redirectUri)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.AppId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = string.Join(",", Scopes),
            ["response_type"] = "code",
        };

        return $"https://www.facebook.com/{_options.GraphVersion}/dialog/oauth" + BuildQuery(query);
    }

    public async Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        // Registered without request logging: steps 1 and 2 are GETs whose
        // query carries the app secret and the code, as Meta's token endpoint
        // expects.
        var http = httpFactory.CreateClient(SocialHttpClients.MetaOAuth);

        // 1. Code → short-lived user token.
        var shortLived = await GetStringFieldAsync(
            http,
            Graph("oauth/access_token", new()
            {
                ["client_id"] = _options.AppId,
                ["client_secret"] = _options.AppSecret,
                ["redirect_uri"] = redirectUri,
                ["code"] = code,
            }),
            "access_token", ct);

        // 2. Short-lived → long-lived (about 60 days).
        //
        // Skipping this step is the classic Meta integration bug: everything
        // works for an hour, then every pharmacy's connection dies at once and
        // it looks like an outage.
        var longLived = await GetStringFieldAsync(
            http,
            Graph("oauth/access_token", new()
            {
                ["grant_type"] = "fb_exchange_token",
                ["client_id"] = _options.AppId,
                ["client_secret"] = _options.AppSecret,
                ["fb_exchange_token"] = shortLived,
            }),
            "access_token", ct);

        // 3. The Pages this user administers, each with its own Page token.
        //    Paged: an agency or a group's head office can administer more
        //    than one page of Pages, and the ones past the first would
        //    otherwise just be missing from the picker.
        var pages = await ListPagesAsync(http, longLived, ct);

        // Who granted this. Meta's deauthorize and data-deletion callbacks
        // name the person, not the Page, so it is recorded on every connection
        // made from this consent.
        var userId = await GetUserIdAsync(http, longLived, ct);

        var discovered = new List<DiscoveredAccount>();

        if (pages.Count == 0)
        {
            log.LogWarning("Meta returned no Pages for this user");
            return discovered;
        }

        foreach (var page in pages)
        {
            var pageId = page.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var pageName = page.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var pageToken = page.TryGetProperty("access_token", out var tokEl) ? tokEl.GetString() : null;

            // A Page without a token is one this user cannot post to. Listing
            // it would offer a connection that fails on first use.
            if (string.IsNullOrWhiteSpace(pageId)
                || string.IsNullOrWhiteSpace(pageName)
                || string.IsNullOrWhiteSpace(pageToken))
                continue;

            discovered.Add(new DiscoveredAccount(
                pageId!,
                pageName!,
                SocialPlatform.FacebookPage,
                pageToken!,
                RefreshToken: null,     // Meta has no refresh token concept here
                ExpiresAtUtc: null,     // Page tokens from a long-lived user token do not expire
                Scopes: Scopes,
                AvatarUrl: PicturePath(page),
                AuthorizedByUserId: userId));

            // The linked Instagram Business account, if there is one. It uses
            // the Page's token, hence ParentAccountId.
            if (page.TryGetProperty("instagram_business_account", out var ig)
                && ig.ValueKind == JsonValueKind.Object
                && ig.TryGetProperty("id", out var igIdEl))
            {
                var igId = igIdEl.GetString();
                var igName = ig.TryGetProperty("username", out var userEl)
                    ? userEl.GetString()
                    : pageName;

                if (!string.IsNullOrWhiteSpace(igId))
                {
                    discovered.Add(new DiscoveredAccount(
                        igId!,
                        string.IsNullOrWhiteSpace(igName) ? pageName! : $"@{igName}",
                        SocialPlatform.InstagramBusiness,
                        pageToken!,
                        RefreshToken: null,
                        ExpiresAtUtc: null,
                        Scopes: Scopes,
                        ParentAccountId: pageId,
                        AvatarUrl: ig.TryGetProperty("profile_picture_url", out var picEl)
                            ? picEl.GetString()
                            : null,
                        AuthorizedByUserId: userId));
                }
            }
        }

        return discovered;
    }

    /* ---------------- helpers ---------------- */

    /// <summary>The app-scoped id of the person who consented.</summary>
    /// <remarks>
    /// Best effort: a failure here is logged and the connection still made,
    /// because refusing a working Page over a missing bookkeeping field would
    /// be the wrong trade. Such a connection is simply not found by a later
    /// deauthorize callback, and fails at its next publish instead.
    /// </remarks>
    private async Task<string?> GetUserIdAsync(HttpClient http, string userToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Graph("me", new()
            {
                ["fields"] = "id",
                ["appsecret_proof"] = MetaAppSecretProof.For(userToken, _options.AppSecret),
            }));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

            using var response = await http.SendAsync(request, ct);
            using var json = await ReadJsonAsync(response, ct);
            return json.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read the consenting Facebook user's id");
            return null;
        }
    }

    /// <summary>Upper bound on Pages read, so a runaway cursor cannot loop for ever.</summary>
    private const int MaxPagePages = 10;

    private async Task<List<JsonElement>> ListPagesAsync(
        HttpClient http, string userToken, CancellationToken ct)
    {
        var pages = new List<JsonElement>();
        string? after = null;

        for (var i = 0; i < MaxPagePages; i++)
        {
            var url = Graph("me/accounts", new()
            {
                ["fields"] = "id,name,access_token,picture{url},instagram_business_account{id,username,profile_picture_url}",
                ["limit"] = "100",
                ["after"] = after,
                ["appsecret_proof"] = MetaAppSecretProof.For(userToken, _options.AppSecret),
            });

            // The token in a header, not the query: this URL is the kind that
            // ends up in traces.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

            using var response = await http.SendAsync(request, ct);
            using var json = await ReadJsonAsync(response, ct);

            if (!json.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                break;

            // Cloned: the document is disposed at the end of this iteration.
            foreach (var page in data.EnumerateArray())
                pages.Add(page.Clone());

            after = json.RootElement.TryGetProperty("paging", out var paging)
                    && paging.TryGetProperty("next", out _)
                    && paging.TryGetProperty("cursors", out var cursors)
                    && cursors.TryGetProperty("after", out var afterEl)
                ? afterEl.GetString()
                : null;

            if (string.IsNullOrEmpty(after)) break;
        }

        return pages;
    }

    private string Graph(string path, Dictionary<string, string?> query) =>
        $"https://graph.facebook.com/{_options.GraphVersion}/{path}" + BuildQuery(query);

    private static string BuildQuery(Dictionary<string, string?> query) =>
        "?" + string.Join("&", query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));

    private static string? PicturePath(JsonElement page) =>
        page.TryGetProperty("picture", out var picture)
        && picture.TryGetProperty("data", out var pdata)
        && pdata.TryGetProperty("url", out var url)
            ? url.GetString()
            : null;

    private async Task<string> GetStringFieldAsync(
        HttpClient http, string url, string field, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        using var json = await ReadJsonAsync(response, ct);

        if (!json.RootElement.TryGetProperty(field, out var value)
            || value.GetString() is not { Length: > 0 } text)
            throw new InvalidOperationException(
                $"Meta's response did not include '{field}'.");

        return text;
    }

    /// <summary>
    /// Parses a Graph response, surfacing Meta's own error text.
    /// </summary>
    /// <remarks>
    /// Meta returns HTTP 400 with a structured error body that says exactly
    /// what is wrong — a redirect URI that doesn't match the app settings, a
    /// scope that wasn't granted. Throwing on the status alone discards the one
    /// piece of information that would fix it.
    /// </remarks>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
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
                $"Meta returned a non-JSON response ({(int)response.StatusCode}).");
        }

        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            json.Dispose();
            throw new InvalidOperationException(message ?? "Meta rejected the request.");
        }

        if (!response.IsSuccessStatusCode)
        {
            json.Dispose();
            throw new InvalidOperationException(
                $"Meta returned {(int)response.StatusCode}.");
        }

        return json;
    }
}
