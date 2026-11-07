using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Application.Videos;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Acme.Infrastructure.VideoPipeline, not .Video — the folder is Video/ but every
// other file in it declares VideoPipeline, and DependencyInjection.cs imports
// that one. See AiRefineClassifier's note.
namespace Acme.Infrastructure.VideoPipeline;

/// <summary>
/// Asks ai-service which voices this deployment can offer.
/// </summary>
/// <remarks>
/// <para>
/// A thin relay. This side deliberately does not read
/// <c>ELEVENLABS_API_KEY</c>: the key decides the narration provider, and a
/// second copy of that decision is what produced the bug — a voice picker
/// offering options the render would ignore, priced at a tenth of their cost.
/// </para>
/// <para>
/// <b>Cached, because the answer is a property of the deployment.</b> It cannot
/// change without a restart of ai-service, and a video dialog that opens is one
/// HTTP hop away from a service that may be busy rendering. Five minutes means a
/// newly-set key appears without anyone clearing anything.
/// </para>
/// </remarks>
public sealed class AiNarrationCatalog(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    IMemoryCache cache,
    ILogger<AiNarrationCatalog> logger) : INarrationCatalog
{
    private readonly AiServiceOptions _options = options.Value;

    /// <summary>The router is mounted at <c>/api/video</c>, not <c>/video</c>.</summary>
    private const string Endpoint = "/api/video/narration";

    private const string CacheKey = "narration-options";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>Short: somebody is waiting for a dialog to open.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<NarrationOptionsDto?> GetAsync(string bearerToken, CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out NarrationOptionsDto? cached) && cached is not null)
            return cached;

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            logger.LogError(
                "Cannot read narration options: AiService__BaseUrl is not set "
                + "(http://ai-service:8000 under docker-compose). The video dialog "
                + "will offer no voice choice.");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            // Forwarded rather than re-minted, so the call stays attached to this
            // pharmacist. ai-service re-checks it.
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "ai-service answered {Status} for narration options.", (int)response.StatusCode);
                return null;
            }

            // Read into a wire shape with a concrete List, then map.
            //
            // The DTO exposes IReadOnlyList because that is the right contract
            // for a value somebody else must not mutate. Whether
            // System.Text.Json can *construct* an IReadOnlyList through a record
            // constructor is a question I could not answer by running it here,
            // and a NotSupportedException at runtime would take the video dialog
            // down for a formatting preference. List<T> is unambiguous.
            var wire = await response.Content.ReadFromJsonAsync<WireResponse>(Json, timeout.Token);
            var body = Map(wire);
            if (body is null)
            {
                // Say which of the two it was.
                //
                // Extracting Map() collapsed a "no voices" message and a "no
                // provider" message into one line that named the second — so
                // half the time the log would have sent somebody to look at the
                // wrong thing. Same fault as every other message in this
                // codebase that asserted more than it had established.
                var reason = wire?.Voices is null || wire.Voices.Count == 0
                    ? "no voices"
                    : "no provider";
                logger.LogWarning(
                    "ai-service returned narration options that cannot be used: {Reason}.", reason);
                return null;
            }

            cache.Set(CacheKey, body, CacheFor);
            return body;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up — a closed tab. Not ours to report.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Narration options timed out after {Seconds}s.", Timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // Including ai-service being down. The dialog copes by offering no
            // choice, which is the right degradation: no choice is honest, and a
            // guessed choice would be the bug this replaced.
            logger.LogWarning(ex, "Could not read narration options from ai-service.");
            return null;
        }
    }

    /// <summary>
    /// Turns what ai-service sent into what the API serves, or null if it
    /// cannot be trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A missing provider is "we could not tell", never "edge".</b>
    /// Defaulting it the cheap-looking way would have the dialog label paid
    /// voices as free and quote $0 for a render that costs $0.05 — the very
    /// under-quote this endpoint exists to remove, reintroduced by a
    /// null-coalesce. That bug was written and then caught by eye; it is pinned
    /// here so the next one is caught by the suite.
    /// </para>
    /// <para>
    /// Public and static so it can be tested without a host or a fake
    /// HttpMessageHandler — the same choice <c>IsReachableFromTheInternet</c>
    /// and <c>ChooseThumbnailSrcAsync</c> make.
    /// </para>
    /// </remarks>
    public static NarrationOptionsDto? Map(WireResponse? wire)
    {
        if (wire?.Voices is null || wire.Voices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(wire.Provider)) return null;

        return new NarrationOptionsDto(
            wire.Provider,
            wire.Voices,
            string.IsNullOrWhiteSpace(wire.DefaultVoiceId)
                ? wire.Voices[0].Id
                : wire.DefaultVoiceId,
            wire.EstimateUsdPerVideo);
    }

    /// <summary>
    /// What ai-service sends, with mutable collections so the deserialiser has
    /// nothing to be clever about. Public only so <see cref="Map"/> is testable.
    /// </summary>
    public sealed record WireResponse(
        string? Provider,
        List<VoiceOptionDto>? Voices,
        string? DefaultVoiceId,
        double EstimateUsdPerVideo);
}
