using System.Security.Cryptography;
using Acme.Application.Auth;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// Reads the <c>signed_request</c> Meta posts to its deauthorize and
/// data-deletion callbacks.
/// </summary>
public interface IMetaSignedRequestReader
{
    /// <summary>
    /// The Facebook user id the request is about, or null when the request
    /// is missing, malformed, not HMAC-SHA256, or not signed with our app
    /// secret. Null is the only answer an unsigned caller ever gets.
    /// </summary>
    string? ReadUserId(string? signedRequest);
}

/// <param name="Url">Where the person can check the request. Meta shows it to them.</param>
/// <param name="ConfirmationCode">The reference Meta shows alongside it.</param>
public sealed record MetaDataDeletionResult(string Url, string ConfirmationCode, int Deleted);

/// <summary>
/// Meta's two platform callbacks: a person removed our app, or asked for their
/// data to be deleted.
/// </summary>
/// <remarks>
/// <para>
/// Required for App Review of any app using Facebook Login, and the right
/// thing regardless: when someone withdraws access at Facebook, we should stop
/// holding their Pages' tokens at once rather than finding out at the next
/// failed publish.
/// </para>
/// <para>
/// Both arrive with the app-scoped Facebook user id, not a Page id, so they
/// act on the connections that person granted
/// (<see cref="SocialConnection.AuthorizedByUserId"/>) — across every pharmacy,
/// since one person may manage the Pages of several. Connections made before
/// that id was recorded cannot be matched; they fail at their next publish
/// with Meta's own "token invalid", and reconnecting records it.
/// </para>
/// </remarks>
public sealed class MetaCallbackService(
    ISocialConnectionRepository connections,
    IAuthLinkBuilder links,
    ILogger<MetaCallbackService> log)
{
    private static readonly SocialPlatform[] MetaPlatforms =
    {
        SocialPlatform.FacebookPage,
        SocialPlatform.InstagramBusiness,
    };

    /// <summary>The person removed our app: revoke, and drop the tokens.</summary>
    /// <returns>How many connections were revoked.</returns>
    public async Task<int> DeauthorizeAsync(string facebookUserId, CancellationToken ct)
    {
        var granted = await connections.ListByAuthorizerAsync(MetaPlatforms, facebookUserId, ct);
        var revoked = 0;

        foreach (var connection in granted.Where(c => c.Status != ConnectionStatus.Revoked))
        {
            connection.Revoke();
            await connections.UpdateAsync(connection, ct);
            revoked++;
        }

        // Counts only: which person and which Pages is exactly what this
        // request asks us to stop keeping.
        log.LogInformation("Meta deauthorize: revoked {Count} connection(s)", revoked);
        return revoked;
    }

    /// <summary>
    /// The person asked for their data to be deleted: remove the connections
    /// outright — tokens, Page names, avatars and ids.
    /// </summary>
    /// <remarks>
    /// Processed before answering, so the status page Meta links to can say
    /// "done" truthfully for any code this issued. Published posts on the
    /// pharmacy's history keep their own record of what was sent, shown
    /// against "(disconnected account)".
    /// </remarks>
    public async Task<MetaDataDeletionResult> DeleteDataAsync(string facebookUserId, CancellationToken ct)
    {
        var granted = await connections.ListByAuthorizerAsync(MetaPlatforms, facebookUserId, ct);

        foreach (var connection in granted)
            await connections.DeleteAsync(connection.Id, ct);

        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        log.LogInformation(
            "Meta data deletion {Code}: deleted {Count} connection(s)", code, granted.Count);

        return new MetaDataDeletionResult(
            links.BuildAppLink($"/privacy/data-deletion?code={code}"), code, granted.Count);
    }
}
