using System.Net.Http.Headers;
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

    /// <summary>
    /// Bytes one request may carry. Mailjet's own ceiling is 15 MB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Counting messages is not enough, because attachments are per message.</b>
    /// Send v3.1 puts <c>InlinedAttachments</c> inside each personalisation, so a
    /// campaign carrying a poster uploads one copy of that poster <i>per
    /// recipient</i>. Fifty recipients and a two-megabyte poster is a hundred
    /// megabytes in a single call.
    /// </para>
    /// <para>
    /// The failure that exposed this looked nothing like a size limit: four
    /// recipients, twenty-one seconds, then <c>Broken pipe</c> — the far end
    /// closing the connection while the body was still being written. Nothing in
    /// that names a payload, and the campaign simply reported every recipient
    /// failed.
    /// </para>
    /// <para>
    /// <b>The budget is a precaution, not a discovered ceiling.</b> This comment
    /// used to claim otherwise: a 1,542,320-byte request failed and a
    /// 1,008,621-byte one succeeded, so a limit was inferred between them. A
    /// later send of 1,008,893 bytes then failed — 272 bytes from the figure
    /// called known-good. The real fault was a pooled connection reused after
    /// the server had closed it, and size only correlated because a longer write
    /// is likelier to be the one holding a dead socket.
    /// </para>
    /// <para>
    /// It stays because a smaller request is exposed for less time and Mailjet's
    /// own 15 MB limit is real, and it is configuration rather than a constant
    /// so an environment can lower it: see
    /// <see cref="MessagingOptions.MailjetSection.MaxRequestBytes"/>. It should
    /// not be tuned as though a precise threshold existed.
    /// </para>
    /// </remarks>
    private long MaxRequestBytes => _options.Mailjet.MaxRequestBytes > 0
        ? _options.Mailjet.MaxRequestBytes
        : 1_000_000;

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
            return new EmailDispatchResult(message.To.Count, 0, "Mock Mailjet — no email actually sent.", Mocked: true);
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

        // Inline images go in a different field. Mailjet distinguishes
        // InlinedAttachments — addressable from the HTML as cid:ContentID — from
        // ordinary Attachments, which are offered to the reader as files. Put a
        // cid: image in the wrong list and the body renders a broken image while
        // the picture sits in the paperclip menu.
        var inlined = (message.InlineImages ?? Array.Empty<EmailInlineImage>())
            .Select(i => new MailjetInlineAttachment(
                i.ContentType,
                // Filename is required by the API and shown nowhere; naming it
                // after the ContentID keeps the two obviously related when
                // reading a raw message source.
                i.ContentId,
                i.ContentId,
                Convert.ToBase64String(i.Content)))
            .ToList();

        var sent = 0;
        var failed = 0;
        string? note = null;

        var recipients = message.To
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        // What one personalisation costs, near enough. The base64 strings are
        // already built, so their lengths are the bulk of it and are exact; the
        // rest is a generous allowance for the JSON scaffolding around them.
        var attachmentBytes =
            attachments.Sum(a => (long)a.Base64Content.Length)
            + inlined.Sum(a => (long)a.Base64Content.Length);

        var perMessageBytes =
            attachmentBytes
            + (message.HtmlBody?.Length ?? 0)
            + (message.TextBody?.Length ?? 0)
            // Room for the unsubscribe link in the body and the two headers.
            + (message.Unsubscribe is null ? 0 : 1024)
            + 512;

        foreach (var batch in ChunkWithinBudget(recipients, perMessageBytes))
        {
            var payload = new MailjetSendRequest(
                SandboxMode: mj.SandboxMode ? true : (bool?)null,
                Messages: batch.Select(to =>
                {
                    // Each person's own unsubscribe link in the body, and the
                    // RFC 8058 headers that give mail clients a one-click
                    // "Unsubscribe" button (security review R2-06).
                    var (html, text) = UnsubscribeLinks.Personalise(message, to);
                    Dictionary<string, string>? headers = null;
                    if (message.Unsubscribe?.TryGetValue(to.ToLowerInvariant(), out var links) == true)
                    {
                        headers = new Dictionary<string, string>
                        {
                            ["List-Unsubscribe"] = $"<{links.OneClickUrl}>",
                            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
                        };
                    }
                    return new MailjetPersonalisation(
                        From: new MailjetAddress(fromAddress, fromName),
                        To:   new[] { new MailjetAddress(to, null) },
                        Subject: message.Subject,
                        TextPart: text,
                        HTMLPart: html,
                        Attachments: attachments.Count == 0 ? null : attachments,
                        InlinedAttachments: inlined.Count == 0 ? null : inlined,
                        Headers: headers);
                }).ToArray());

            // Serialised up front rather than streamed. See BuildRequest — this
            // is what stops the upload going out chunked, and it also means the
            // size below is measured rather than estimated.
            var requestBytes = MeasureRequest(payload);

            _logger.LogInformation(
                "Mailjet request: {Count} message(s), {Size:N0} bytes with Content-Length.",
                batch.Count, requestBytes);

            try
            {
                using var res = await SendWithOneRetryAsync(
                    payload, batch.Count, requestBytes, ct);

                // Read out here, deliberately outside the retry. See
                // SendWithOneRetryAsync: once the response headers exist the
                // messages may already be accepted, and a retry from that point
                // would send the campaign twice.
                var body = await SafeReadBody(res, ct);

                if (!res.IsSuccessStatusCode)
                {
                    // Mailjet's API answers in JSON, always, including its
                    // errors. An HTML body did not come from Mailjet — it came
                    // from something in between that terminated the connection
                    // and answered on its behalf.
                    //
                    // This matters because the two need opposite responses. A
                    // Mailjet 4xx is ours to fix: a bad key, an unverified
                    // sender, a malformed payload. A gateway's 502 after twenty
                    // seconds of uploading is a proxy or a scanner giving up on
                    // a request that was taking too long, and no amount of
                    // editing this class will help.
                    var fromIntermediary = LooksLikeHtml(body);

                    _logger.LogWarning(
                        "{Source} returned {Status} for batch of {Count} ({Size:N0} bytes): {Body}",
                        fromIntermediary ? "An intermediary (not Mailjet)" : "Mailjet",
                        (int)res.StatusCode, batch.Count, requestBytes, Truncate(body, 400));

                    if (fromIntermediary)
                    {
                        _logger.LogWarning(
                            "That response is HTML. Mailjet answers in JSON, so this came from a "
                            + "proxy, gateway or TLS-inspecting scanner between this host and "
                            + "api.mailjet.com — most likely one giving up on a slow upload. "
                            + "Lowering Messaging:Mailjet:MaxRequestBytes shortens each request "
                            + "and is the usual way through; scripts/mailjet-probe.sh confirms it "
                            + "with curl, outside this application.");
                    }

                    failed += batch.Count;
                    note ??= fromIntermediary
                        ? "Blocked between here and Mailjet — see server logs."
                        : $"Mailjet {(int)res.StatusCode}: {Truncate(body, 240)}";
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
                // "Broken pipe" means the far end closed the connection while we
                // were still writing the body. It is the *symptom*, and it is
                // worth being explicit that it does not identify the cause:
                // a server that has already decided to reject a request — bad
                // credentials, a size limit, an unsupported encoding — and does
                // not drain the body produces exactly this, and the status code
                // it wanted to send us is lost because we never get to read a
                // response.
                //
                // Sending with Content-Length and Expect: 100-continue is what
                // gives the server a chance to answer *before* the payload, so
                // repeat failures here should now carry a real status instead.
                var broken = IsConnectionClosedMidUpload(ex);

                _logger.LogError(ex,
                    "Mailjet send failed for {Count} message(s), {Size:N0} bytes. {Hint}",
                    batch.Count, requestBytes,
                    broken
                        ? "The server closed the connection mid-upload. This is not necessarily "
                          + "a size problem — verify the API key and secret are still valid, and "
                          + "check for a proxy between this host and api.mailjet.com."
                        : "See the exception for the cause.");

                failed += batch.Count;
                note ??= broken
                    ? "Mailjet closed the connection before the message finished uploading. "
                      + "See server logs."
                    : "Could not reach Mailjet — see server logs.";
            }
        }

        return new EmailDispatchResult(sent, failed, note);
    }

    /* --------------------------- helpers --------------------------- */

    /// <summary>
    /// Sends, and tries once more if the connection died while uploading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this exists for is a connection the server had already
    /// closed.</b> A pooled keep-alive that Mailjet dropped is indistinguishable
    /// from a live one until something is written to it, and a request large
    /// enough to take several seconds to upload is long enough for the reset to
    /// arrive mid-body. It surfaces as <c>Broken pipe</c> from inside the write.
    /// </para>
    /// <para>
    /// <b>Retrying is safe here, and it is worth being precise about why.</b>
    /// The exception comes from <c>SendRequestContentAsync</c> — the body was
    /// still being written, so it never arrived complete and Mailjet cannot have
    /// parsed it, let alone queued anything. There is no risk of sending a
    /// campaign twice.
    /// </para>
    /// <para>
    /// <b><see cref="HttpCompletionOption.ResponseHeadersRead"/> is what makes
    /// that true.</b> The default reads the whole response inside
    /// <c>SendAsync</c>, which would put "the connection broke while Mailjet was
    /// telling us it had accepted four messages" inside this catch — and
    /// retrying that sends the campaign a second time. Returning at the headers
    /// puts the retry boundary exactly where the guarantee is: nothing has been
    /// accepted yet. The body is read by the caller, outside this method.
    /// </para>
    /// <para>
    /// Once, not a loop with backoff. The second attempt opens a new connection,
    /// which is the entire remedy; if that also dies the problem is not a stale
    /// socket and pretending otherwise would turn a clear failure into a slow
    /// one.
    /// </para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendWithOneRetryAsync(
        MailjetSendRequest payload, int messageCount, long requestBytes, CancellationToken ct)
    {
        try
        {
            using var first = BuildRequest(payload, out _);
            return await _http.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && IsConnectionClosedMidUpload(ex))
        {
            _logger.LogWarning(
                "Mailjet connection closed while uploading {Count} message(s), {Size:N0} bytes. "
                + "Retrying once on a fresh connection — this is what a reused keep-alive that "
                + "the server had already dropped looks like.",
                messageCount, requestBytes);

            // A new HttpRequestMessage: one cannot be sent twice, and its
            // content stream has already been consumed by the failed attempt.
            using var second = BuildRequest(payload, out _);
            return await _http.SendAsync(second, HttpCompletionOption.ResponseHeadersRead, ct);
        }
    }

    /// <summary>Bytes the request will put on the wire.</summary>
    private static long MeasureRequest(MailjetSendRequest payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts).LongLength;

    /// <summary>
    /// Whether a response body is an HTML page rather than Mailjet's JSON.
    /// </summary>
    /// <remarks>
    /// Sniffed from the body, not from Content-Type, because the header is set
    /// by whatever produced the page and a middlebox is not obliged to be
    /// truthful about it. The body is unambiguous: Mailjet has never answered
    /// with a document that starts <c>&lt;html</c>.
    /// </remarks>
    private static bool LooksLikeHtml(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var head = body.TrimStart();
        return head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<head", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the send request with a known <c>Content-Length</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists instead of <c>PostAsJsonAsync</c>, and the difference is
    /// the whole point.</b> <c>JsonContent</c> cannot report its length before
    /// it is written, so <c>HttpClient</c> falls back to
    /// <c>Transfer-Encoding: chunked</c> and serialises the poster straight
    /// onto the socket. A failure then surfaces as
    /// <c>IOException: Broken pipe</c> raised from inside
    /// <c>JsonTypeInfo.SerializeAsync</c> — which names the JSON serialiser,
    /// the one component that is definitely not at fault, and discards whatever
    /// status the server was trying to return.
    /// </para>
    /// <para>
    /// Serialising to a byte array first costs one copy of a message we have
    /// already built in memory, and buys three things: a real
    /// <c>Content-Length</c>, so a server can refuse an oversized request with
    /// a status instead of a reset; an exact size for the log rather than an
    /// estimate; and a request some gateways will accept where they would have
    /// rejected a chunked body outright.
    /// </para>
    /// <para>
    /// <c>Expect: 100-continue</c> is set for the same reason. It asks the
    /// server to accept or refuse the headers before we upload a megabyte and a
    /// half of poster, which turns a silent connection reset into a readable
    /// <c>401</c> or <c>413</c>. It costs one extra round trip per batch, which
    /// against a campaign send is not measurable.
    /// </para>
    /// </remarks>
    private static HttpRequestMessage BuildRequest(
        MailjetSendRequest payload, out long requestBytes)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        requestBytes = json.LongLength;

        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        // Set explicitly. ByteArrayContent computes it, but the assignment is
        // what this method exists for and a future edit should have to delete
        // a visible line rather than silently change a content type.
        content.Headers.ContentLength = json.LongLength;

        return new HttpRequestMessage(HttpMethod.Post, "/v3.1/send")
        {
            Content = content,
            Headers = { ExpectContinue = true },
        };
    }

    /// <summary>
    /// Whether the far end closed the connection while the body was uploading.
    /// </summary>
    /// <remarks>
    /// Matched on the exception chain rather than on the message text, which is
    /// localised and differs between platforms — "Broken pipe" on Unix,
    /// "An existing connection was forcibly closed" on Windows.
    /// </remarks>
    private static bool IsConnectionClosedMidUpload(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException or IOException) return true;
        }
        return false;
    }

    /// <summary>
    /// Groups recipients so no single request exceeds what Mailjet will accept.
    /// </summary>
    /// <remarks>
    /// A message larger than the whole budget is still sent, alone and with a
    /// warning. Splitting it further is impossible — one recipient is the
    /// smallest unit — so the choice is between trying and failing loudly, or
    /// dropping it silently. Trying is right: the limit is approximate, and the
    /// log then names the size rather than leaving a broken pipe to explain.
    /// </remarks>
    private IEnumerable<List<string>> ChunkWithinBudget(
        IReadOnlyList<string> recipients, long perMessageBytes)
    {
        if (perMessageBytes >= MaxRequestBytes)
        {
            _logger.LogWarning(
                "A single message is about {Size:N0} bytes, at or over the {Limit:N0} byte "
                + "request budget. Sending one recipient at a time; expect Mailjet to refuse "
                + "it. Shrink the poster or drop the inline image.",
                perMessageBytes, MaxRequestBytes);
        }

        var perBatch = perMessageBytes <= 0
            ? BatchSize
            : (int)Math.Clamp(MaxRequestBytes / perMessageBytes, 1, BatchSize);

        if (perBatch < BatchSize && recipients.Count > perBatch)
        {
            _logger.LogInformation(
                "Sending {Count} recipients in batches of {PerBatch}: each message carries "
                + "about {Size:N0} bytes of body and attachments.",
                recipients.Count, perBatch, perMessageBytes);
        }

        return Chunk(recipients, perBatch);
    }

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
        [property: JsonPropertyName("Attachments")] IReadOnlyList<MailjetAttachment>? Attachments,
        [property: JsonPropertyName("InlinedAttachments")] IReadOnlyList<MailjetInlineAttachment>? InlinedAttachments,
        [property: JsonPropertyName("Headers")] IReadOnlyDictionary<string, string>? Headers = null);

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

    /// <summary>Same shape plus a ContentID, which is what `cid:` resolves against.</summary>
    private sealed record MailjetInlineAttachment(
        [property: JsonPropertyName("ContentType")]   string ContentType,
        [property: JsonPropertyName("Filename")]      string Filename,
        [property: JsonPropertyName("ContentID")]     string ContentID,
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
