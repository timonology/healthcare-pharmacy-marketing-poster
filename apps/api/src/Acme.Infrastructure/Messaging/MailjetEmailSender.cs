using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Acme.Application.Messaging;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Messaging;

/// <summary>
/// Mailjet Send API v3.1 implementation.
///
/// Docs: https://dev.mailjet.com/email/guides/send-api-v31/
///
/// Auth is HTTP Basic with API_KEY:SECRET_KEY. We ship one HTTP call per
/// batch of up to <see cref="BatchSize"/> personalised messages so no
/// recipient sees another recipient's address in the "To" line.
/// </summary>
public sealed class MailjetEmailSender : IEmailSender
{
    /// <summary>
    /// Mailjet allows up to 50 personalised messages in a single Send v3.1 call.
    /// Keeping headroom for retries.
    /// </summary>
    private const int BatchSize = 50;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly MessagingOptions _options;
    private readonly ILogger<MailjetEmailSender> _logger;

    public MailjetEmailSender(
        HttpClient http,
        IOptions<MessagingOptions> options,
        ILogger<MailjetEmailSender> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        var mj = _options.Mailjet;
        if (string.IsNullOrWhiteSpace(mj.ApiKey) || string.IsNullOrWhiteSpace(mj.SecretKey))
        {
            // We fail loudly at first send rather than at DI, so a mis-configured
            // deployment still boots and can serve /health, patient CRUD, etc.
            _logger.LogWarning(
                "Mailjet credentials are missing. Configure Messaging:Mailjet:ApiKey " +
                "and Messaging:Mailjet:SecretKey (via env vars) before sending email.");
        }
        else
        {
            var raw = $"{mj.ApiKey}:{mj.SecretKey}";
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(raw));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(mj.BaseUrl))
            _http.BaseAddress = new Uri(mj.BaseUrl);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Catch the most common misconfig: pasting the docs URL (dev.mailjet.com)
        // instead of the API host (api.mailjet.com). Warn loudly at boot so it
        // surfaces before someone tries to send a campaign.
        var host = _http.BaseAddress?.Host ?? string.Empty;
        if (host.Equals("dev.mailjet.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "Messaging:Mailjet:BaseUrl is set to https://dev.mailjet.com — that is the docs " +
                "site, not the API. Change it to https://api.mailjet.com (or delete the key to " +
                "use the default).");
        }
        else if (!string.IsNullOrEmpty(host)
                 && !host.EndsWith("mailjet.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Messaging:Mailjet:BaseUrl points at {Host}, which doesn't look like a Mailjet host. " +
                "Expected https://api.mailjet.com.", host);
        }
    }

    public async Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (_options.UseMock)
        {
            _logger.LogInformation(
                "[MOCK Mailjet] from={From} to={Count} subject={Subject} attachments={Attachments}",
                message.FromAddress, message.To.Count, message.Subject, message.Attachments.Count);
            return new EmailDispatchResult(message.To.Count, 0, "Mock Mailjet — no email actually sent.");
        }

        var mj = _options.Mailjet;
        if (string.IsNullOrWhiteSpace(mj.ApiKey) || string.IsNullOrWhiteSpace(mj.SecretKey))
        {
            return new EmailDispatchResult(0, message.To.Count,
                "Mailjet credentials missing. Set Messaging__Mailjet__ApiKey and __SecretKey.");
        }

        // Prefer the sender identity declared on the incoming EmailMessage, but
        // fall back to the configured verified sender if the caller left it blank.
        var fromAddress = string.IsNullOrWhiteSpace(message.FromAddress) ? mj.FromAddress : message.FromAddress;
        var fromName = string.IsNullOrWhiteSpace(message.FromName) ? mj.FromName : message.FromName;

        // Attachments are shared across all personalisations in this batch.
        var attachments = message.Attachments
            .Select(a => new MailjetAttachment(a.ContentType, a.FileName, Convert.ToBase64String(a.Content)))
            .ToList();

        var sent = 0;
        var failed = 0;
        string? note = null;

        var recipients = message.To
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        foreach (var batch in Chunk(recipients, BatchSize))
        {
            var payload = new MailjetSendRequest(
                SandboxMode: mj.SandboxMode ? true : (bool?)null,
                Messages: batch.Select(to => new MailjetPersonalisation(
                    From: new MailjetAddress(fromAddress, fromName),
                    To:   new[] { new MailjetAddress(to, null) },
                    Subject: message.Subject,
                    TextPart: message.TextBody,
                    HTMLPart: message.HtmlBody,
                    Attachments: attachments.Count == 0 ? null : attachments)).ToArray());

            try
            {
                using var res = await _http.PostAsJsonAsync("/v3.1/send", payload, JsonOpts, ct);
                var body = await SafeReadBody(res, ct);

                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Mailjet returned {Status} for batch of {Count}: {Body}",
                        (int)res.StatusCode, batch.Count, body);
                    failed += batch.Count;
                    note ??= $"Mailjet {(int)res.StatusCode}: {Truncate(body, 240)}";
                    continue;
                }

                // Even on 200, Mailjet returns per-message status. We must
                // parse before claiming anything was sent.
                _logger.LogInformation("Mailjet 200 response body: {Body}", Truncate(body, 1000));

                MailjetSendResponse? parsed = null;
                try { parsed = JsonSerializer.Deserialize<MailjetSendResponse>(body, JsonOpts); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Mailjet returned 200 but the response was not JSON we could parse. Treating as failed.");
                    failed += batch.Count;
                    note ??= "Mailjet returned an unrecognised response body.";
                    continue;
                }

                if (parsed?.Messages is null || parsed.Messages.Count == 0)
                {
                    // Empty Messages array = we can't confirm delivery.
                    _logger.LogWarning(
                        "Mailjet 200 had no per-message status for {Count} recipients. " +
                        "Treating as failed to avoid false 'Sent' claims. Body: {Body}",
                        batch.Count, Truncate(body, 240));
                    failed += batch.Count;
                    note ??= "Mailjet accepted the request but returned no delivery confirmation.";
                    continue;
                }

                var acceptedIds = new List<long>();
                foreach (var status in parsed.Messages)
                {
                    if (string.Equals(status.Status, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        sent++;
                        if (status.To is { Count: > 0 })
                            acceptedIds.AddRange(status.To.Select(t => t.MessageID));
                    }
                    else
                    {
                        failed++;
                        var reason = status.Errors is { Count: > 0 } errs
                            ? errs[0].ErrorMessage
                            : status.Status;
                        _logger.LogWarning("Mailjet rejected message: {Reason}", reason);
                        note ??= $"Mailjet: {reason}";
                    }
                }

                if (acceptedIds.Count > 0 && note is null)
                {
                    // Give the campaign UI something users can hand to Mailjet
                    // support / paste into the dashboard to trace delivery.
                    var preview = string.Join(", ", acceptedIds.Take(3));
                    note = acceptedIds.Count <= 3
                        ? $"Mailjet accepted. IDs: {preview}"
                        : $"Mailjet accepted. First IDs: {preview}, +{acceptedIds.Count - 3} more.";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mailjet send failed for batch of {Count}", batch.Count);
                failed += batch.Count;
                note ??= "Mailjet transport error — see server logs.";
            }
        }

        return new EmailDispatchResult(sent, failed, note);
    }

    /* --------------------------- helpers --------------------------- */

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");

    /* ----------------------- wire types (v3.1) --------------------- */

    private sealed record MailjetSendRequest(
        [property: JsonPropertyName("SandboxMode")] bool? SandboxMode,
        [property: JsonPropertyName("Messages")]    MailjetPersonalisation[] Messages);

    private sealed record MailjetPersonalisation(
        [property: JsonPropertyName("From")]        MailjetAddress From,
        [property: JsonPropertyName("To")]          IReadOnlyList<MailjetAddress> To,
        [property: JsonPropertyName("Subject")]     string Subject,
        [property: JsonPropertyName("TextPart")]    string? TextPart,
        [property: JsonPropertyName("HTMLPart")]    string? HTMLPart,
        [property: JsonPropertyName("Attachments")] IReadOnlyList<MailjetAttachment>? Attachments);

    private sealed record MailjetAddress(
        [property: JsonPropertyName("Email")] string Email,
        [property: JsonPropertyName("Name")]  string? Name);

    private sealed record MailjetToStatus(
        [property: JsonPropertyName("Email")]     string? Email,
        [property: JsonPropertyName("MessageID")] long   MessageID);

    private sealed record MailjetAttachment(
        [property: JsonPropertyName("ContentType")]   string ContentType,
        [property: JsonPropertyName("Filename")]      string Filename,
        [property: JsonPropertyName("Base64Content")] string Base64Content);

    private sealed record MailjetSendResponse(
        [property: JsonPropertyName("Messages")] IReadOnlyList<MailjetMessageStatus>? Messages);

    private sealed record MailjetMessageStatus(
        [property: JsonPropertyName("Status")] string? Status,
        [property: JsonPropertyName("To")]     IReadOnlyList<MailjetToStatus>? To,
        [property: JsonPropertyName("Errors")] IReadOnlyList<MailjetError>? Errors);

    private sealed record MailjetError(
        [property: JsonPropertyName("ErrorMessage")] string? ErrorMessage);
}
