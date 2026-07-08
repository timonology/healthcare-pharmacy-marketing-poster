namespace Acme.Infrastructure.Options;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>When true, no email or SMS is actually sent (dispatch is logged only).</summary>
    public bool UseMock { get; set; } = true;

    /// <summary>Email provider selector: "smtp" (default) or "mailjet".</summary>
    public string Provider { get; set; } = "smtp";

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
        public string FromAddress { get; set; } = "no-reply@pharmacyposter.app";
        public string FromName { get; set; } = "Sonar Marketing25";
    }

    public sealed class MailjetSection
    {
        /// <summary>Mailjet public API key. Set via env var, never commit.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Mailjet private secret key. Set via env var, never commit.</summary>
        public string? SecretKey { get; set; }

        /// <summary>Verified sender email registered in your Mailjet account.</summary>
        public string FromAddress { get; set; } = "no-reply@pharmacyposter.app";
        public string FromName { get; set; } = "Sonar Marketing25";

        /// <summary>API base — override only for staging/sandbox routing.</summary>
        public string BaseUrl { get; set; } = "https://api.mailjet.com";

        /// <summary>Optional Mailjet SandboxMode — messages are validated but not sent.</summary>
        public bool SandboxMode { get; set; }
    }

    public sealed class SmsSection
    {
        public string Provider { get; set; } = "mock";
        public string? AccountSid { get; set; }
        public string? AuthToken { get; set; }
        public string SenderId { get; set; } = "PHARMACY";
    }
}
