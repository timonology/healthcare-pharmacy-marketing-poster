using Acme.Application.Campaigns;
using Acme.Application.Invitations;
using Acme.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Whether outbound email is actually configured to work, and a way to prove it.
/// </summary>
/// <remarks>
/// <para>
/// Email is the worst thing in this system to get wrong, because every failure
/// mode looks like success from the inside. The invitation is created, the row
/// says "Invited", the log line says the send was attempted — and the
/// pharmacist never hears from us. Three separate settings can each cause that
/// silently:
/// </para>
/// <list type="bullet">
/// <item><c>Messaging:UseMock</c> defaults to <b>true</b>, so an environment
/// that never sets it logs every email and sends none.</item>
/// <item>Mailjet credentials missing — the send returns Failed, which only
/// appears if someone reads the response.</item>
/// <item>No <c>Public:WebBaseUrl</c> and no CORS origin, so activation links
/// are built against <c>http://localhost:3000</c> and are useless to the
/// recipient.</item>
/// </list>
/// <para>
/// The GET here reports all three. It asks the real
/// <see cref="IInvitationLinkBuilder"/> for a real link rather than
/// re-deriving the URL, because a check that reimplements the thing it is
/// checking can agree with itself while both are wrong.
/// </para>
/// <para>
/// The POST actually sends. It is SuperAdmin-only and rate-limited by nothing
/// but that — an anonymous endpoint that sends mail to an arbitrary address is
/// a spam relay, however convenient it would be for a smoke test.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/diagnostics")]
[Authorize(Policy = AuthPolicies.SuperAdminOnly)]
public sealed class EmailDiagnosticsController(
    IConfiguration config,
    IHostEnvironment env,
    IEmailSender email,
    IInvitationLinkBuilder links) : AdminApiController
{
    /// <summary>Is outbound email configured to actually leave the building?</summary>
    [HttpGet("email")]
    public IActionResult EmailConfig()
    {
        var provider = config["Messaging:Provider"] ?? "smtp";
        var useMock = !string.Equals(config["Messaging:UseMock"], "false", StringComparison.OrdinalIgnoreCase);
        var isMailjet = string.Equals(provider, "mailjet", StringComparison.OrdinalIgnoreCase);

        var section = isMailjet ? "Messaging:Mailjet" : "Messaging:Smtp";
        var fromAddress = config[$"{section}:FromAddress"];
        // Including the fallback names the sender honours (MailjetSettingAliases),
        // so this agrees with what a send will actually do.
        bool Has(string canonical, string[] aliases) =>
            !string.IsNullOrWhiteSpace(config[canonical])
            || aliases.Any(a => !string.IsNullOrWhiteSpace(config[a]));

        var hasCredentials = isMailjet
            ? Has("Messaging:Mailjet:ApiKey", Acme.Infrastructure.Messaging.MailjetSettingAliases.ApiKey)
              && Has("Messaging:Mailjet:SecretKey", Acme.Infrastructure.Messaging.MailjetSettingAliases.SecretKey)
            : !string.IsNullOrWhiteSpace(config["Messaging:Smtp:Host"]);

        // A throwaway token: the shape of the link is the point, and the value
        // is never stored or accepted anywhere.
        var sampleLink = links.BuildActivationLink("diagnostic-sample-token");
        var linkIsLocalhost = sampleLink.Contains("localhost", StringComparison.OrdinalIgnoreCase);

        var problems = new List<string>();

        if (useMock)
            problems.Add(
                "Messaging:UseMock is not \"false\", so no email is being sent. It defaults to " +
                "true — an environment that omits it sends nothing and logs instead.");

        if (!hasCredentials)
            problems.Add(isMailjet
                ? "Mailjet credentials are missing. Set Messaging__Mailjet__ApiKey and Messaging__Mailjet__SecretKey."
                : "No SMTP host configured. Set Messaging__Smtp__Host.");

        if (string.IsNullOrWhiteSpace(fromAddress))
            problems.Add($"No from-address. Set {section.Replace(":", "__")}__FromAddress to a sender " +
                         "your provider has verified — an unverified sender is rejected at send time.");

        if (linkIsLocalhost && !env.IsDevelopment())
            problems.Add(
                "Activation links point at localhost. Set Public__WebBaseUrl to the public web " +
                "address, or the invitation emails cannot be acted on by anyone who receives them.");

        if (string.Equals(config["Messaging:Mailjet:SandboxMode"], "true", StringComparison.OrdinalIgnoreCase))
            problems.Add(
                "Messaging:Mailjet:SandboxMode is true. Mailjet validates each message and reports " +
                "success without delivering it.");

        return Ok(new
        {
            provider,
            useMock,
            hasCredentials,
            fromAddress = fromAddress ?? "(missing)",
            // The link the recipient would actually be given, token aside.
            sampleActivationLink = sampleLink,
            sandboxMode = string.Equals(
                config["Messaging:Mailjet:SandboxMode"], "true", StringComparison.OrdinalIgnoreCase),
            environment = env.EnvironmentName,
            wouldSend = !useMock && hasCredentials && !string.IsNullOrWhiteSpace(fromAddress),
            problems,
        });
    }

    /// <summary>
    /// The campaign email, rendered, so it can be looked at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout in email is not something you can review by reading the markup.
    /// Whether the poster is centred, whether it overflows on a phone, whether
    /// the video sits sensibly under it — those are visual facts, and the only
    /// honest way to check them is to look. This returns the output of the real
    /// <see cref="CampaignEmail.BuildHtml"/>, so what you see is what gets sent.
    /// </para>
    /// <para>
    /// The poster is a placeholder of the same proportions, passed in as the
    /// image URL. It used to be a <c>cid:</c> reference that this method had to
    /// find and replace afterwards, because a cid resolves against a MIME part
    /// that exists in a message and not in a browser. Now that the real email
    /// links its poster too, the preview gives the builder a URL exactly as a
    /// send does, and there is nothing to substitute after the fact.
    /// </para>
    /// <para>
    /// Anonymous would make this a way to render arbitrary text into an HTML
    /// page served from our own origin, so it inherits the SuperAdmin policy on
    /// the class.
    /// </para>
    /// </remarks>
    [HttpGet("campaign-email/preview")]
    [Produces("text/html")]
    public IActionResult CampaignEmailPreview(
        [FromQuery] int videos = 1,
        [FromQuery] bool poster = true)
    {
        var videoLinks = Enumerable.Range(1, Math.Clamp(videos, 0, 5))
            .Select(i => new VideoEmailLink(
                $"How to use your inhaler ({i})",
                $"https://example.test/watch/{i}?token=preview",
                PlaceholderImage(320, 180, "#0e7490")))
            .ToList();

        // A placeholder that is both the image and the link, so the preview
        // shows the shape a real send produces: the design, wrapped in a link,
        // with the "Not showing?" line under it. Following the link in the
        // preview opens the placeholder, which is the honest analogue of
        // following it in a real email and getting the poster.
        var placeholder = poster ? PlaceholderImage(600, 800, "#1e293b") : null;

        var html = CampaignEmail.BuildHtml(
            "Winter flu clinic — book now",
            "Winter flu poster",
            "Riverside Pharmacy",
            placeholder is null
                ? null
                : new CampaignEmail.CampaignPosterImage(placeholder, placeholder),
            videoLinks,
            // Real sends carry each recipient's own link here.
            unsubscribeUrl: "#unsubscribe");

        return Content(html, "text/html");
    }

    /// <summary>A plain SVG data URL standing in for a real image.</summary>
    private static string PlaceholderImage(int width, int height, string fill)
    {
        var svg =
            $"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}' " +
            $"viewBox='0 0 {width} {height}'>" +
            $"<rect width='{width}' height='{height}' fill='{fill}'/>" +
            $"<text x='50%' y='50%' fill='#ffffff' font-family='sans-serif' font-size='20' " +
            $"text-anchor='middle' dominant-baseline='middle'>{width}&#215;{height}</text></svg>";

        return "data:image/svg+xml;base64," +
               Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
    }

    /// <param name="To">Recipient.</param>
    /// <param name="InlineKb">
    /// Optional ballast, in kilobytes, attached inline so the request is the
    /// size a real campaign would be. Omit or 0 for a plain test. See
    /// <c>BallastImage</c>.
    /// </param>
    public sealed record EmailTestRequest(string To, int InlineKb = 0);

    /// <summary>
    /// Sends a real test email and reports what the provider said about it.
    /// </summary>
    /// <remarks>
    /// Returns the provider's own verdict rather than "queued". The Mailjet
    /// sender deliberately refuses to count a message as sent without
    /// per-recipient confirmation, so a <c>sent: 1</c> here means Mailjet
    /// accepted it for that address — which is the strongest claim anything
    /// server-side can honestly make.
    /// </remarks>
    [Audit("admin.diagnostics.email_test")]
    [HttpPost("email/test")]
    public async Task<IActionResult> SendTest(
        [FromBody] EmailTestRequest body, CancellationToken ct)
    {
        if (Context() is null) return Unauthorized();

        var to = body?.To?.Trim();
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
            return BadRequest(new { error = "A recipient email address is required." });

        var provider = config["Messaging:Provider"] ?? "smtp";
        var section = string.Equals(provider, "mailjet", StringComparison.OrdinalIgnoreCase)
            ? "Messaging:Mailjet"
            : "Messaging:Smtp";

        var sample = links.BuildActivationLink("diagnostic-sample-token");
        var stamp = DateTime.UtcNow.ToString("u");

        var result = await email.SendAsync(
            new EmailMessage(
                FromAddress: config[$"{section}:FromAddress"] ?? string.Empty,
                FromName: config[$"{section}:FromName"] ?? "Marketing25",
                To: new[] { to },
                Subject: "Marketing25 — email delivery test",
                HtmlBody:
                    "<p>This is a delivery test from Marketing25.</p>" +
                    "<p>If you are reading it, outbound email works and invitation " +
                    "emails will reach their recipients.</p>" +
                    $"<p>Activation links on this environment look like:<br>" +
                    $"<code>{System.Net.WebUtility.HtmlEncode(sample)}</code></p>" +
                    $"<p style=\"color:#666\">Sent {stamp} from {env.EnvironmentName}.</p>",
                TextBody:
                    "This is a delivery test from Marketing25.\n\n" +
                    "If you are reading it, outbound email works and invitation emails " +
                    "will reach their recipients.\n\n" +
                    $"Activation links on this environment look like:\n{sample}\n\n" +
                    $"Sent {stamp} from {env.EnvironmentName}.",
                Attachments: Array.Empty<EmailAttachment>(),
                InlineImages: BallastImage(body?.InlineKb ?? 0)),
            ct);

        // 200 either way: the request succeeded, and the interesting answer is
        // in the body. A 500 here would be indistinguishable from the endpoint
        // itself being broken.
        return Ok(new
        {
            accepted = result.Sent > 0,
            result.Sent,
            result.Failed,
            note = result.Note ?? "No note from the provider.",
            sentTo = to,
            inlineKb = body?.InlineKb ?? 0,
        });
    }

    /// <summary>
    /// An inline image of a requested size, for bisecting a send that only
    /// fails on large messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A campaign carrying a poster failed against Mailjet with
    /// <c>Broken pipe</c> — the connection closed while the body was still
    /// uploading — at a request size of 1.5 MB, which is far below any
    /// documented limit. A plain test email is the wrong instrument for that:
    /// it is two kilobytes, so it proves the credentials and the route work
    /// and says nothing about the failure.
    /// </para>
    /// <para>
    /// This makes the payload the variable. Send at 0, then 250, 500, 1000,
    /// 2000 KB and find where it turns over. A clean 0 and a failure at 1000
    /// is a size or gateway problem; failure at every size including 0 is
    /// credentials or connectivity, and the poster was a red herring.
    /// </para>
    /// <para>
    /// Random bytes rather than zeroes, deliberately. A block of zeroes
    /// compresses to nothing over TLS, so it would not reproduce the transfer
    /// of a real poster's worth of bytes.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<EmailInlineImage> BallastImage(int inlineKb)
    {
        if (inlineKb <= 0) return Array.Empty<EmailInlineImage>();

        // Capped. This endpoint is SuperAdmin-only, but an unbounded number
        // here is an easy way to allocate the process to death by typo.
        var bytes = new byte[Math.Clamp(inlineKb, 1, 20_000) * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

        // Not a real PNG. It is never rendered — the point is the byte count on
        // the wire, and no client will display an image this message's HTML
        // does not reference.
        return new[] { new EmailInlineImage("diagnostic-ballast", "image/png", bytes) };
    }
}
