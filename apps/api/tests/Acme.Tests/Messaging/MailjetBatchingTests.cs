using System.Net;
using System.Text;
using System.Text.Json;
using Acme.Application.Messaging;
using Acme.Infrastructure.Messaging;
using Acme.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Messaging;

/// <summary>
/// What the Mailjet sender actually puts on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file exists because a campaign to four recipients failed in
/// production and nothing in the suite noticed.</b> The cause was that Send
/// v3.1 carries <c>InlinedAttachments</c> inside each personalisation, so a
/// poster is uploaded once per recipient, and batching was capped at fifty
/// messages with no regard for bytes. Fifty recipients and a two-megabyte
/// poster is a hundred megabytes in one request; Mailjet closed the connection
/// mid-upload and the only evidence was <c>Broken pipe</c>.
/// </para>
/// <para>
/// Three separate things hid it, and each is worth naming because each is a
/// pattern rather than an oversight:
/// </para>
/// <list type="number">
///   <item>
///     the integration host replaces <c>IEmailSender</c> with a capturing fake,
///     so this class never ran in any test — the suite verifies everything up
///     to the sender and then substitutes it;
///   </item>
///   <item>
///     every campaign test sends to exactly <i>one</i> recipient, and the bug
///     needs two before it exists at all;
///   </item>
///   <item>
///     <c>EmailSenderTests</c> — the file whose name suggests it covers this —
///     is a source scan that explicitly skips <c>MailjetEmailSender.cs</c>.
///   </item>
/// </list>
/// <para>
/// So the sender is exercised here directly, against a stub transport. No
/// network, no Mailjet account: the assertions are about the requests this code
/// chooses to make.
/// </para>
/// </remarks>
public class MailjetBatchingTests
{
    /// <summary>Records every request instead of sending it.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(int Messages, long Bytes)> Requests { get; } = new();

        /// <summary>What each request declared before its body was read.</summary>
        public List<(long? ContentLength, bool? ExpectContinue)> Declared { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // Captured before reading: reading buffers the body, after which any
            // content would report a length and the check would prove nothing.
            Declared.Add((request.Content!.Headers.ContentLength, request.Headers.ExpectContinue));
            var body = await request.Content!.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var count = doc.RootElement.GetProperty("Messages").GetArrayLength();
            Requests.Add((count, Encoding.UTF8.GetByteCount(body)));

            // Mailjet's shape, so the sender's parsing runs too rather than
            // bailing out early and leaving half this class unexercised.
            var statuses = string.Join(",", Enumerable.Repeat(
                """{"Status":"success","To":[{"MessageID":1}]}""", count));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"Messages":[{{statuses}}]}"""),
            };
        }
    }

    private static (MailjetEmailSender Sender, RecordingHandler Handler) NewSender()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.mailjet.test") };

        var options = Options.Create(new MessagingOptions
        {
            // UseMock defaults to true, and a mocked sender returns before it
            // builds a single request — every assertion below would pass while
            // testing nothing at all.
            UseMock = false,
            Provider = "mailjet",
            Mailjet = new MessagingOptions.MailjetSection
            {
                ApiKey = "test-key",
                SecretKey = "test-secret",
                FromAddress = "noreply@example.test",
                FromName = "Marketing25",
                BaseUrl = "https://api.mailjet.test",
            },
        });

        return (
            new MailjetEmailSender(http, options, NullLogger<MailjetEmailSender>.Instance),
            handler);
    }

    private static EmailMessage MessageFor(int recipients, int inlineBytes)
    {
        var inline = inlineBytes == 0
            ? Array.Empty<EmailInlineImage>()
            : new[] { new EmailInlineImage("campaign-poster", "image/png", new byte[inlineBytes]) };

        return new EmailMessage(
            FromAddress: "",
            FromName: "Riverside Pharmacy",
            To: Enumerable.Range(0, recipients).Select(i => $"p{i}@example.test").ToList(),
            Subject: "Flu clinic",
            HtmlBody: "<html><body>Flu clinic</body></html>",
            TextBody: "Flu clinic",
            Attachments: Array.Empty<EmailAttachment>(),
            InlineImages: inline);
    }

    /// <summary>Mailjet refuses a request over 15 MB. Ours must stay under it.</summary>
    private const long MailjetLimitBytes = 15L * 1024 * 1024;

    [Fact]
    public async Task A_campaign_with_a_poster_never_exceeds_what_Mailjet_accepts()
    {
        // The exact shape that failed: several recipients, one poster each.
        // Two megabytes is an ordinary A4 render at the width this app uses.
        var (sender, handler) = NewSender();

        await sender.SendAsync(MessageFor(recipients: 12, inlineBytes: 2 * 1024 * 1024),
            CancellationToken.None);

        handler.Requests.Should().NotBeEmpty("the send must actually happen");

        foreach (var (messages, bytes) in handler.Requests)
        {
            bytes.Should().BeLessThan(MailjetLimitBytes,
                "a request of {0:N0} bytes carrying {1} message(s) is what Mailjet closes "
                + "the connection on — and it surfaces as 'Broken pipe' with nothing "
                + "naming the size", bytes, messages);
        }
    }

    [Fact]
    public async Task Every_request_declares_its_length_up_front()
    {
        // PostAsJsonAsync streamed chunked: a server that refused the request
        // and closed left us with "Broken pipe" from inside the JSON serialiser,
        // naming the one component not at fault and losing the server's status.
        // A declared length lets the server answer with a status instead.
        var (sender, handler) = NewSender();

        await sender.SendAsync(MessageFor(recipients: 60, inlineBytes: 600_000), CancellationToken.None);

        handler.Declared.Should().NotBeEmpty();
        handler.Declared.Should().OnlyContain(d => d.ContentLength > 0,
            "a chunked upload turns a refusal into a connection reset");
        handler.Declared.Should().OnlyContain(d => d.ExpectContinue == true,
            "100-continue lets the server refuse before a megabyte is uploaded");
    }

    [Fact]
    public async Task Every_recipient_is_still_sent_to_once()
    {
        // The obvious way to satisfy the test above is to send fewer emails.
        // Splitting into more requests must not drop or duplicate anybody.
        var (sender, handler) = NewSender();

        var result = await sender.SendAsync(
            MessageFor(recipients: 12, inlineBytes: 2 * 1024 * 1024), CancellationToken.None);

        handler.Requests.Sum(r => r.Messages).Should().Be(12,
            "splitting a batch must not lose a recipient");
        result.Sent.Should().Be(12);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public async Task A_big_poster_is_split_into_more_requests_than_a_small_one()
    {
        // The behaviour itself: the batch size follows the payload. Before this,
        // both of these went out as a single request of fifty.
        // Eight rather than fifty: the ratio is what is under test, and each
        // extra recipient costs a couple of megabytes of allocation in a suite
        // that should stay quick.
        var (heavySender, heavy) = NewSender();
        await heavySender.SendAsync(
            MessageFor(recipients: 8, inlineBytes: 2 * 1024 * 1024), CancellationToken.None);

        var (lightSender, light) = NewSender();
        await lightSender.SendAsync(
            MessageFor(recipients: 8, inlineBytes: 20 * 1024), CancellationToken.None);

        heavy.Requests.Count.Should().BeGreaterThan(light.Requests.Count,
            "a heavier attachment has to mean fewer recipients per request");
    }

    [Fact]
    public async Task Without_attachments_the_batching_stays_generous()
    {
        // The other direction. A plain text email has no reason to be split, and
        // a size rule that shrank every batch would turn one request into fifty
        // for no benefit.
        var (sender, handler) = NewSender();

        await sender.SendAsync(MessageFor(recipients: 50, inlineBytes: 0), CancellationToken.None);

        handler.Requests.Should().HaveCount(1,
            "fifty small messages still fit in one call");
    }

    /// <summary>The size that actually failed, measured rather than assumed.</summary>
    /// <remarks>
    /// A campaign to one recipient failed against Mailjet at <b>1,542,320</b>
    /// bytes; minutes later, on the same machine, one of <b>1,008,621</b> bytes
    /// succeeded. So the real ceiling on that path is around a tenth of the
    /// 15 MB Mailjet documents.
    /// <para>
    /// This is here because the batching budget was 10 MB at the time, and 10 MB
    /// is not wrong by a little — with a 174 KB poster it would have built
    /// requests of 41 recipients and about 10 MB, seven times the size of one
    /// already known to fail. It looked correct only because the campaigns being
    /// tested had four recipients. A limit nobody has measured is a guess with
    /// units on it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Requests_stay_under_the_size_that_was_observed_to_fail()
    {
        const int ObservedFailureBytes = 1_542_320;
        const int ObservedSuccessBytes = 1_008_621;

        // The real poster after the email re-encode, and a list long enough that
        // the old 10 MB budget would have put every recipient in one request.
        var (sender, handler) = NewSender();

        await sender.SendAsync(
            MessageFor(recipients: 40, inlineBytes: 174_057), CancellationToken.None);

        handler.Requests.Should().NotBeEmpty();

        foreach (var (messages, bytes) in handler.Requests)
        {
            bytes.Should().BeLessThan(ObservedFailureBytes,
                "{0:N0} bytes across {1} message(s) is at or past the size that was seen "
                + "to be cut off mid-upload; {2:N0} is the largest that has actually been "
                + "observed to work", bytes, messages, ObservedSuccessBytes);
        }
    }

    [Fact]
    public async Task A_single_message_larger_than_the_budget_is_still_attempted()
    {
        // One recipient is the smallest unit, so an oversized message cannot be
        // split. Sending it and letting Mailjet answer is better than dropping
        // it silently — the pharmacist gets a failure they can act on rather
        // than a campaign that reports success and delivers nothing.
        var (sender, handler) = NewSender();

        await sender.SendAsync(
            MessageFor(recipients: 1, inlineBytes: 12 * 1024 * 1024), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Messages.Should().Be(1);
    }
}
