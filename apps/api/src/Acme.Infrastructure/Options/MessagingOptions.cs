namespace Acme.Infrastructure.Options;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>When true, no email or SMS is actually sent (dispatch is logged only).</summary>
    public bool UseMock { get; set; } = true;

    /// <summary>Email provider selector: "smtp" (default) or "mailjet".</summary>
    public string Provider { get; set; } = "smtp";

    /// <summary>
    /// Width the campaign poster is re-encoded to before it is served.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The email displays it at 600px, so 800 covers a high-DPI phone at the
    /// width it is actually shown and anything above that is bytes nobody
    /// sees.
    /// </para>
    /// <para>
    /// <b>This setting stopped being about the send.</b> It was the only lever
    /// on what a campaign uploaded, because Mailjet carries a copy of an
    /// inline poster inside every recipient's personalisation — 244 KB per
    /// recipient, measured, and a four-recipient campaign that took eighty
    /// seconds to upload before the provider closed the connection. The poster
    /// is a link now, served from <c>/api/posters/email-image</c>, so the whole
    /// of that multiplication is gone whatever this is set to.
    /// </para>
    /// <para>
    /// What it governs now is the download <i>each reader</i> pays for, on
    /// whatever connection they are on, which is a better thing for it to
    /// govern. Lowered from 1200 with the same change: nothing depends on it
    /// being generous any more, and a patient opening an email on mobile data
    /// notices the difference.
    /// </para>
    /// </remarks>
    public int PosterMaxWidthPx { get; set; } = 800;

    /// <summary>
    /// JPEG quality for that re-encode, 1-100.
    /// </summary>
    /// <remarks>
    /// 65 at 800px is about a third of the bytes of 78 at 1200px and still
    /// clean on a phone. Going much under 60 starts to tell around text, which
    /// is most of what a pharmacy poster is.
    /// </remarks>
    public int PosterJpegQuality { get; set; } = 65;

    public SmtpSection Smtp { get; set; } = new();
    public MailjetSection Mailjet { get; set; } = new();
    public SmsSection Sms { get; set; } = new();

    public sealed class SmtpSection
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 25;
        public bool EnableSsl { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        /// <summary>
        /// No default. The address must come from configuration.
        /// </summary>
        /// <remarks>
        /// This used to default to <c>no-reply@pharmacyposter.app</c>, a domain
        /// not verified with the provider. An environment missing the setting
        /// therefore fell back to an address every send would be refused for —
        /// and refused at the provider, where nothing in the application can
        /// see it. Empty fails visibly instead.
        /// </remarks>
        public string FromAddress { get; set; } = "";
        public string FromName { get; set; } = "Marketing25";
    }

    public sealed class MailjetSection
    {
        /// <summary>Mailjet public API key. Set via env var, never commit.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Mailjet private secret key. Set via env var, never commit.</summary>
        public string? SecretKey { get; set; }

        /// <summary>
        /// Verified sender email registered in your Mailjet account.
        /// </summary>
        /// <remarks>
        /// Empty by default, deliberately — see the SMTP section above. A
        /// plausible-looking default for an unverified domain is worse than no
        /// default, because the failure is invisible from inside the app.
        /// </remarks>
        public string FromAddress { get; set; } = "";
        public string FromName { get; set; } = "Marketing25";

        /// <summary>API base — override only for staging/sandbox routing.</summary>
        public string BaseUrl { get; set; } = "https://api.mailjet.com";

        /// <summary>Optional Mailjet SandboxMode — messages are validated but not sent.</summary>
        public bool SandboxMode { get; set; }

        /// <summary>
        /// Largest request this app will build, in bytes. Recipients are split
        /// across as many calls as it takes to stay under it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Mailjet documents 15 MB. Do not set this to 15 MB.</b> That figure
        /// is the limit at Mailjet; it says nothing about what survives the
        /// path in between, and the path is where this has actually failed.
        /// </para>
        /// <para>
        /// <b>This is not a measured ceiling, and an earlier version of this
        /// comment claimed it was.</b> A request of 1,008,621 bytes succeeded
        /// and one of 1,542,320 failed, so a limit was inferred between them.
        /// A later send of <b>1,008,893 bytes then failed</b> — 272 bytes from
        /// the figure called known-good. Two points are not a threshold.
        /// </para>
        /// <para>
        /// The real fault was a pooled keep-alive connection being reused after
        /// the server had dropped it; see the <c>SocketsHttpHandler</c>
        /// configuration in <c>DependencyInjection</c>. Size only ever
        /// correlated because a bigger body spends longer being written, and so
        /// is likelier to be the one holding a dead socket when the reset lands.
        /// </para>
        /// <para>
        /// A budget is still worth keeping: fewer bytes means a shorter write
        /// and less exposure, and Mailjet's own 15 MB limit is real. But it is a
        /// sensible default rather than a number derived from evidence, and it
        /// should not be described as one. Too low costs a few extra HTTP calls;
        /// too high risks a campaign reporting every recipient as failed.
        /// </para>
        /// </remarks>
        public long MaxRequestBytes { get; set; } = 1_000_000;


    }

    public sealed class SmsSection
    {
        public string Provider { get; set; } = "mock";
        public string? AccountSid { get; set; }
        public string? AuthToken { get; set; }
        public string SenderId { get; set; } = "PHARMACY";
    }
}
