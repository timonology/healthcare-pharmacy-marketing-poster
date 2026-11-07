using Acme.Application.Common;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// What TikTok says about the account a post will go to, for the share
/// dialog: whose account it is, which audiences it may choose, and which
/// interactions the creator has switched off.
/// </summary>
/// <remarks>
/// TikTok's Content Sharing Guidelines require the posting screen to show
/// these live, every time — never a cached or assumed set of options.
/// </remarks>
public sealed record TikTokCreatorInfo(
    string Username,
    string Nickname,
    string? AvatarUrl,
    IReadOnlyList<string> PrivacyLevelOptions,
    bool CommentDisabled,
    bool DuetDisabled,
    bool StitchDisabled,
    int MaxVideoPostDurationSec);

/// <summary>TikTok's answer, or why there is none.</summary>
public sealed record TikTokCreatorInfoResult(
    TikTokCreatorInfo? Info,
    string? Error,
    bool IsAuthFailure = false,
    RenewedCredential? Renewed = null);

/// <summary>Asks TikTok for an account's creator info with its stored credential.</summary>
public interface ITikTokCreatorInfoClient
{
    Task<TikTokCreatorInfoResult> GetAsync(string credential, CancellationToken ct);
}

/// <summary>Creator info for one of the caller's own TikTok connections.</summary>
public sealed class TikTokCreatorService(
    ISocialConnectionRepository connections,
    ITokenCipher cipher,
    ITikTokCreatorInfoClient client,
    ILogger<TikTokCreatorService> log)
{
    public async Task<Result<TikTokCreatorInfo>> GetAsync(string ownerId, string connectionId, CancellationToken ct)
    {
        var connection = await connections.FindByIdAsync(connectionId, ct);
        // Not found and not-yours are one answer, as everywhere in social.
        if (connection is null || connection.OwnerId != ownerId || connection.Status == ConnectionStatus.Revoked
            || connection.Platform != SocialPlatform.TikTokAccount)
            return Result<TikTokCreatorInfo>.NotFound("That TikTok account is not connected.");

        if (connection.Status == ConnectionStatus.NeedsReauth || connection.AccessToken is null)
            return Result<TikTokCreatorInfo>.Invalid($"{connection.DisplayName} needs to be reconnected before it can be posted to.");

        var used = connection.AccessToken;
        string credential;
        try
        {
            credential = cipher.Decrypt(connection.AccessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not decrypt credentials for connection {Id}", connection.Id);
            return Result<TikTokCreatorInfo>.Invalid("Stored credentials could not be read. Reconnect the account.");
        }

        var result = await client.GetAsync(credential, ct);

        if (result.Renewed is { } renewed)
        {
            var encrypted = cipher.Encrypt(renewed.Credential);
            connection.RenewCredential(encrypted, renewed.ExpiresAtUtc);
            await connections.RenewCredentialAsync(connection.Id, encrypted, renewed.ExpiresAtUtc, CancellationToken.None);
        }

        if (result.Info is { } info) return Result<TikTokCreatorInfo>.Success(info);

        if (result.IsAuthFailure)
        {
            // Rotated by a publish at the same moment: the account is fine.
            if (result.Renewed is null
                && await ConnectionWrites.RotatedElsewhereAsync(connections, connection.Id, used))
                return Result<TikTokCreatorInfo>.Invalid("TikTok's sign-in was just renewed. Try again in a moment.");

            connection.MarkNeedsReauth(result.Error ?? "TikTok no longer accepts this sign-in.");
            await ConnectionWrites.SaveStatusAsync(connections, connection);
        }

        return Result<TikTokCreatorInfo>.Invalid(result.Error ?? "TikTok did not answer. Try again in a moment.");
    }
}
