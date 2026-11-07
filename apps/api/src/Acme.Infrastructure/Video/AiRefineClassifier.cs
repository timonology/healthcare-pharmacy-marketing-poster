using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Application.Videos;
using Acme.Domain.Videos;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Acme.Infrastructure.VideoPipeline, not .Video — the folder is Video/ but
// every other file in it declares VideoPipeline, and DependencyInjection.cs
// imports that one. A second namespace for a single class would compile and
// then fail to resolve at registration.
namespace Acme.Infrastructure.VideoPipeline;

/// <summary>
/// Asks the Python ai-service which pipeline stage a change request needs.
/// </summary>
/// <remarks>
/// <para>
/// Rendering is dispatched through Mongo, because nobody is watching. This is
/// the other kind: a pharmacist has typed what they want changed and is waiting
/// to be told what it will do. So it is a request/response call, for the same
/// reason template generation is one.
/// </para>
/// <para>
/// A thin transport. The stage that comes back is a <i>suggestion</i>; whether
/// it may be entered is decided by <see cref="RefineStages"/> on this side.
/// </para>
/// </remarks>
public sealed class AiRefineClassifier(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    ILogger<AiRefineClassifier> logger) : IRefineClassifier
{
    private readonly AiServiceOptions _options = options.Value;

    /// <summary>
    /// The router is mounted at <c>/api/video</c>, not <c>/video</c>.
    /// </summary>
    /// <remarks>
    /// FastAPI declares <c>prefix="/video"</c> on the router and main.py mounts
    /// it with <c>prefix="/api"</c>. Reading only the router's own prefix gives
    /// the wrong answer and every call 404s — which is exactly what the canvas
    /// client's comment already records, so it is written down twice on
    /// purpose.
    /// </remarks>
    private const string Endpoint = "/api/video/classify-refine";

    /// <summary>
    /// Short on purpose.
    /// </summary>
    /// <remarks>
    /// Someone is watching a spinner. A classification that has not come back in
    /// fifteen seconds is not going to be worth waiting for, and "we could not
    /// work out what to change, try again" is a better answer than a page that
    /// hangs. Template generation's 210 seconds is for a different job with a
    /// different audience.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string CouldNotTell =
        "We could not work out what to change just now. Please try again.";

    public async Task<RefineRouting> ClassifyAsync(
        string instruction, string bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            // A configuration fault, not the pharmacist's. Loud in the log,
            // gentle on screen — they cannot do anything about it either way.
            logger.LogError(
                "Cannot classify a refine: AiService__BaseUrl is not set "
                + "(http://ai-service:8000 under docker-compose, "
                + "http://localhost:8000 when running ai-service directly).");
            return new RefineRouting(null, CouldNotTell);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new { instruction }, options: Json),
            };
            // Forwarded rather than re-minted, so the downstream call stays
            // attached to this pharmacist. ai-service re-checks it.
            message.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await http.SendAsync(message, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                // 401 gets its own line. Folded into the generic warning it
                // reads as "the classifier could not understand", when what it
                // means is that we sent no usable token — a configuration fault
                // that looks exactly like a working feature having an off day.
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden)
                {
                    logger.LogError(
                        "ai-service rejected the refine classification as {Status}. "
                        + "The caller's bearer token was not accepted — check that "
                        + "the API and ai-service share Jwt:Secret, Jwt:Issuer and "
                        + "Jwt:Audience.", (int)response.StatusCode);
                }
                else
                {
                    logger.LogWarning(
                        "Refine classification answered {Status}.", (int)response.StatusCode);
                }
                return new RefineRouting(null, CouldNotTell);
            }

            var body = await response.Content.ReadFromJsonAsync<Payload>(Json, timeout.Token);
            if (body is null) return new RefineRouting(null, CouldNotTell);

            var reason = string.IsNullOrWhiteSpace(body.Reason) ? CouldNotTell : body.Reason;

            // "unclear" is the service's own way of declining to guess, and it
            // arrives as a stage name that will not parse. Both paths land here
            // and mean the same thing: ask the pharmacist to rephrase.
            var stage = RefineStages.Parse(body.Stage);
            return new RefineRouting(stage, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up — a closed browser tab. Not ours to report.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Refine classification timed out after {Seconds}s.",
                Timeout.TotalSeconds);
            return new RefineRouting(null, CouldNotTell);
        }
        catch (Exception ex)
        {
            // Including the service being down. A refine that cannot be routed
            // is a refine that does not happen, and the video the pharmacist
            // already has is untouched — which is the right outcome.
            logger.LogWarning(ex, "Refine classification failed.");
            return new RefineRouting(null, CouldNotTell);
        }
    }

    private sealed record Payload(string? Stage, string? Reason, bool Free);
}
