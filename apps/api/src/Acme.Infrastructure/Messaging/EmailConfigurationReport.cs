using Acme.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Messaging;

/// <summary>
/// Mailjet credentials kept under a name other than the one this API reads.
/// </summary>
/// <remarks>
/// <para>
/// The API reads <c>Messaging__Mailjet__ApiKey</c> and
/// <c>Messaging__Mailjet__SecretKey</c>. Production reported "Mailjet credentials
/// missing" while its operator could see Mailjet keys in the App Service
/// settings — the classic shape of a value stored under a different name
/// (<c>MAILJET_API_KEY</c>, the names this repo's own Azure scripts take,
/// <c>Mailjet__ApiKey</c>, or Mailjet's SDK names). Nothing in the portal says
/// a setting is unread.
/// </para>
/// <para>
/// So those names are honoured as a fallback, and only when the canonical
/// setting is empty — the canonical name always wins. Each use is logged at
/// startup by name, never by value, with the name to rename it to. This is a
/// bridge for an environment mid-migration, not a second convention.
/// </para>
/// </remarks>
public static class MailjetSettingAliases
{
    public static readonly string[] ApiKey =
    {
        "Mailjet:ApiKey", "MAILJET_API_KEY", "MAILJET_KEY", "MJ_APIKEY_PUBLIC", "Messaging:Mailjet:PublicKey",
    };

    public static readonly string[] SecretKey =
    {
        "Mailjet:SecretKey", "MAILJET_SECRET_KEY", "MAILJET_SECRET", "MJ_APIKEY_PRIVATE",
        "Messaging:Mailjet:ApiSecret", "Messaging:Mailjet:PrivateKey",
    };

    /// <summary>Fills empty Mailjet credentials from an alias; returns the aliases used.</summary>
    public static IReadOnlyList<string> Apply(MessagingOptions options, IConfiguration config)
    {
        var used = new List<string>();
        var mj = options.Mailjet;

        if (string.IsNullOrWhiteSpace(mj.ApiKey) && First(config, ApiKey) is { } key)
        {
            mj.ApiKey = config[key];
            used.Add(key);
        }
        if (string.IsNullOrWhiteSpace(mj.SecretKey) && First(config, SecretKey) is { } secret)
        {
            mj.SecretKey = config[secret];
            used.Add(secret);
        }
        return used;
    }

    private static string? First(IConfiguration config, IEnumerable<string> names) =>
        names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(config[n]));

    /// <summary>As an environment variable is written: ':' becomes '__'.</summary>
    public static string EnvName(string key) => key.Replace(":", "__");
}

/// <summary>
/// One line at startup saying whether this API can send email, and if not, why
/// — by setting name, never by value.
/// </summary>
/// <remarks>
/// The same idea as <c>SocialConfigurationReport</c>. The first sign of a
/// missing Mailjet key used to be a campaign marked Failed, or a password reset
/// that never arrived, hours later and far from the cause.
/// </remarks>
internal sealed class EmailConfigurationReport(
    IOptions<MessagingOptions> options,
    IConfiguration config,
    IHostEnvironment env,
    IHttpClientFactory http,
    ILogger<EmailConfigurationReport> log) : IHostedService
{
    /// <summary>
    /// Is the from-address a sender this Mailjet account has verified?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure this exists for is the quietest one email has: Mailjet
    /// <b>accepts</b> a message from an unverified sender — the API answers
    /// success, campaigns say Sent, the smoke test passes — and then holds it.
    /// Nothing arrives and nothing in this application can see why. That is
    /// what happened when production began reading appsettings.Production.json
    /// and with it a sender Mailjet had never verified.
    /// </para>
    /// <para>
    /// Asked of Mailjet's own sender list, for the exact address and for the
    /// domain (<c>*@domain</c>, which is how a whole domain is verified). In the
    /// background with a short timeout, so a slow Mailjet never delays startup.
    /// </para>
    /// </remarks>
    private async Task CheckSenderAsync(MessagingOptions o)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (verified, detail) = await MailjetProbe.SenderAsync(http.CreateClient(Readiness.ReadinessService.HttpClientName), o, cts.Token);
        if (verified == true) log.LogInformation("Mailjet: {Detail}.", detail);
        else log.LogWarning("Mailjet: {Detail}.", detail);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var isMailjet = string.Equals(o.Provider, "mailjet", StringComparison.OrdinalIgnoreCase);

        // Every setting whose name mentions Mailjet, so a misnamed one is
        // visible next to the verdict. Names only.
        var mailjetNames = config.AsEnumerable()
            .Where(kv => kv.Key.Contains("mailjet", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => MailjetSettingAliases.EnvName(kv.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var problems = new List<string>();
        if (o.UseMock) problems.Add("Messaging__UseMock is not \"false\" — every email is logged, none sent");
        if (isMailjet && string.IsNullOrWhiteSpace(o.Mailjet.ApiKey)) problems.Add("Messaging__Mailjet__ApiKey is missing");
        if (isMailjet && string.IsNullOrWhiteSpace(o.Mailjet.SecretKey)) problems.Add("Messaging__Mailjet__SecretKey is missing");
        if (isMailjet && string.IsNullOrWhiteSpace(o.Mailjet.FromAddress)) problems.Add("Messaging__Mailjet__FromAddress is missing");

        if (problems.Count == 0)
        {
            log.LogInformation(
                "Email can be sent: provider {Provider}, from {From}, environment {Environment}.",
                o.Provider, isMailjet ? o.Mailjet.FromAddress : o.Smtp.FromAddress, env.EnvironmentName);

            // Fire and forget: the answer is a log line, never a reason to wait.
            if (isMailjet && !o.Mailjet.SandboxMode)
                _ = Task.Run(() => CheckSenderAsync(o), CancellationToken.None);
        }
        else
            log.LogWarning(
                "Email will NOT be sent: {Problems}. Environment {Environment}. Mailjet settings present: {Present}.",
                string.Join("; ", problems), env.EnvironmentName,
                mailjetNames.Count == 0 ? "none" : string.Join(", ", mailjetNames));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Asks Mailjet whether the configured sender is verified on the account the
/// keys belong to. Shared by the startup report and the readiness check.
/// </summary>
/// <remarks>
/// Mailjet <b>accepts</b> a message from an unverified sender — the API answers
/// success, campaigns say Sent — and then holds it, so nothing arrives and
/// nothing in this application can see why. Asked of Mailjet's own sender
/// list, for the exact address and for the domain (<c>*@domain</c>, which is
/// how a whole domain is verified). Free: a read of account settings.
/// </remarks>
public static class MailjetProbe
{
    /// <returns>
    /// <c>true</c> verified, <c>false</c> not verified or the keys were refused,
    /// <c>null</c> when Mailjet could not be asked. Never throws.
    /// </returns>
    public static async Task<(bool? Verified, string Detail)> SenderAsync(
        HttpClient client, MessagingOptions o, CancellationToken ct)
    {
        var from = o.Mailjet.FromAddress?.Trim() ?? "";
        var at = from.IndexOf('@');
        if (at <= 0) return (false, "no sender address (Messaging__Mailjet__FromAddress)");

        try
        {
            var host = Uri.TryCreate(o.Mailjet.BaseUrl, UriKind.Absolute, out var b) ? b.Host : "api.mailjet.com";
            var statuses = new List<string>();
            foreach (var candidate in new[] { from, "*" + from[at..] })
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://{host}/v3/REST/sender?Email={Uri.EscapeDataString(candidate)}");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{o.Mailjet.ApiKey}:{o.Mailjet.SecretKey}")));
                using var res = await client.SendAsync(req, ct);

                if ((int)res.StatusCode is 401 or 403)
                    return (false, "Mailjet refused the keys (Messaging__Mailjet__ApiKey / SecretKey are wrong)");
                if (!res.IsSuccessStatusCode)
                    return (null, $"could not ask Mailjet about {from}: HTTP {(int)res.StatusCode}");

                using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("Data", out var data))
                    foreach (var s in data.EnumerateArray())
                        if (s.TryGetProperty("Status", out var st) && st.GetString() is { } value)
                            statuses.Add(value);
            }

            return statuses.Any(s => s.Equals("Active", StringComparison.OrdinalIgnoreCase))
                ? (true, $"sender {from} is verified on this account")
                : (false,
                    $"sender {from} is NOT verified on this account " +
                    $"({(statuses.Count == 0 ? "not registered" : string.Join("/", statuses))}) — Mailjet accepts " +
                    "mail from it and then holds it. Verify it in Mailjet or change Messaging__Mailjet__FromAddress");
        }
        catch (Exception ex)
        {
            // Never throws: the startup report runs it unawaited.
            return (null, ex is OperationCanceledException
                ? $"Mailjet did not answer in time about {from}"
                : $"could not ask Mailjet about {from}: {ex.Message}");
        }
    }
}
