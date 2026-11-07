using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Messaging;

/// <summary>
/// No email may name its own sender.
/// </summary>
/// <remarks>
/// <para>
/// <c>MailjetEmailSender</c> and <c>SmtpEmailSender</c> both resolve the sender
/// message-first: a non-empty <c>FromAddress</c> on the message overrides the
/// configured one. That is a reasonable design and a dangerous default, because
/// the failure is entirely invisible from inside the application — the code
/// runs, the send is attempted, and the <i>provider</i> refuses it because the
/// domain is not verified on the account.
/// </para>
/// <para>
/// That is exactly what happened: <c>InvitationEmail</c> and the poster-share
/// endpoint both hardcoded <c>no-reply@pharmacyposter.app</c> while the
/// verified sender was <c>info@sonarinformatics.com</c>. Invitations were
/// created, tokens were issued, nothing was delivered, and no exception was
/// ever thrown.
/// </para>
/// <para>
/// A source-level test rather than a behavioural one, deliberately. The bug is
/// a literal in a constructor argument; asserting on the shape of the source is
/// the only way to catch the <i>next</i> email that does it, rather than the
/// two that already did.
/// </para>
/// </remarks>
public class EmailSenderTests
{
    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Acme.Application")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must be able to find apps/api/src to read");
        return dir!.FullName;
    }

    private static IEnumerable<(string File, string Line, int Number)> FromAddressLines()
    {
        var root = Path.Combine(SourceRoot(), "src");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            // The senders themselves are where the fallback lives; they are
            // allowed — indeed required — to mention a configured address.
            var name = Path.GetFileName(file);
            if (name is "MailjetEmailSender.cs" or "SmtpEmailSender.cs" or "IEmailSender.cs")
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // Skip comments. Documentation explaining this rule mentions
                // FromAddress constantly, and a scan that cannot tell code
                // from prose punishes writing the explanation down — which is
                // exactly how the sibling test below first failed.
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal))
                    continue;

                if (line.Contains("FromAddress:", StringComparison.Ordinal))
                    yield return (name, line, i + 1);
            }
        }
    }

    [Fact]
    public void There_are_emails_to_check()
    {
        // The assertion below passes trivially on an empty sequence, and a
        // reflection-free source scan is exactly the kind of check that
        // silently stops finding anything when a folder moves.
        FromAddressLines().Should().NotBeEmpty(
            "the application sends email, so something must set FromAddress");
    }

    [Fact]
    public void No_email_hardcodes_its_sender_address()
    {
        // Anything that is not empty-string or a config lookup. A literal
        // address here is a message the provider will refuse.
        var offenders = FromAddressLines()
            .Where(l => !Regex.IsMatch(
                l.Line,
                @"FromAddress:\s*(string\.Empty|""""|FromAddress\b|config\[|_?options\.|opts\.)"))
            .Select(l => $"{l.File}:{l.Number}  {l.Line}")
            .ToList();

        offenders.Should().BeEmpty(
            "an email that names its own sender overrides the verified one and is " +
            "rejected by the provider, with no error visible inside the app. Pass " +
            "string.Empty and let Messaging:Mailjet:FromAddress decide");
    }

    [Fact]
    public void No_sender_constant_holds_an_address()
    {
        // Closes the gap in the scan above. `FromAddress: FromAddress` passes
        // it — the value is a constant, not a literal — so the constant itself
        // has to be checked, or the bug simply moves up one line.
        var root = Path.Combine(SourceRoot(), "src");
        var declaration = new Regex(
            @"const\s+string\s+From(Address|Name)\s*=\s*""(?<value>[^""]*)""");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is "MailjetEmailSender.cs" or "SmtpEmailSender.cs" or "IEmailSender.cs")
                continue;

            foreach (Match m in declaration.Matches(File.ReadAllText(file)))
            {
                if (m.Groups["value"].Value.Length > 0)
                    offenders.Add($"{name}  {m.Value}");
            }
        }

        offenders.Should().BeEmpty(
            "a sender constant with a value in it overrides the configured sender " +
            "just as surely as a literal does. Leave it empty");
    }

    /// <summary>
    /// Every code path that sends email, and the test that watches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The invitation sender bug survived because <c>InvitationService</c> had
    /// no test that looked at the message it built — the only invitation test
    /// in the suite parsed a CSV. Coverage was not thin there by decision;
    /// nobody had noticed it was absent.
    /// </para>
    /// <para>
    /// So this is a ratchet rather than a check of anything in particular.
    /// Taking an <c>IEmailSender</c> now obliges you to name the test that
    /// captures what you send, and the named file has to exist — you cannot
    /// satisfy it with a plausible string.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> EmailPathsWithTests = new()
    {
        ["CampaignService.cs"] = "CampaignEmailDeliveryTests.cs",
        ["AccountEmailService.cs"] = "AccountEmailTests.cs",
        ["InvitationService.cs"] = "InvitationEmailTests.cs",
        ["PosterExportController.cs"] = "PosterShareEmailTests.cs",

        // Recurring posts: posted / needs you / reconnect needed / paused.
        // Captured through a recording IEmailSender — subject, once-only, the
        // problems-only setting, and escaping of what the pharmacy typed.
        ["SeriesNotifier.cs"] = "RecurringSeriesTests.cs",

        // The diagnostic endpoint exists to send a test message on demand, so
        // "does it send" is the whole of its behaviour and a test would assert
        // the thing it is. It is SuperAdmin-only and covered by the
        // authorisation matrix, which is the part that matters.
        ["EmailDiagnosticsController.cs"] = "",
    };

    private static IEnumerable<string> FilesTakingAnEmailSender()
    {
        var root = Path.Combine(SourceRoot(), "src");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            // The senders themselves, the interface, and the composition root
            // mention IEmailSender without being a path that sends anything.
            if (name is "MailjetEmailSender.cs" or "SmtpEmailSender.cs"
                     or "IEmailSender.cs" or "DependencyInjection.cs")
                continue;

            if (File.ReadAllText(file).Contains("IEmailSender", StringComparison.Ordinal))
                yield return name;
        }
    }

    [Fact]
    public void Every_path_that_sends_email_names_a_test()
    {
        var unlisted = FilesTakingAnEmailSender()
            .Where(f => !EmailPathsWithTests.ContainsKey(f))
            .ToList();

        unlisted.Should().BeEmpty(
            "these take an IEmailSender and no test is claimed for them. That is how " +
            "the invitation email shipped undeliverable for months. Add an entry to " +
            "EmailPathsWithTests naming the test file that captures what they send");
    }

    [Fact]
    public void Every_named_email_test_actually_exists()
    {
        // Without this the map is a list of good intentions. Naming a test that
        // was renamed, moved or never written would satisfy the ratchet above
        // while covering nothing.
        var testsRoot = Path.Combine(SourceRoot(), "tests");
        var present = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = EmailPathsWithTests
            .Where(kv => kv.Value.Length > 0 && !present.Contains(kv.Value))
            .Select(kv => $"{kv.Key} claims {kv.Value}, which does not exist")
            .ToList();

        missing.Should().BeEmpty("a named test that is not there covers nothing");
    }

    [Fact]
    public void The_email_path_list_has_no_stale_entries()
    {
        // The inverse. An entry left behind after a file is deleted or stops
        // sending email quietly widens what the ratchet permits, and the next
        // file to take that name inherits the exemption.
        var actual = FilesTakingAnEmailSender().ToHashSet(StringComparer.Ordinal);

        var stale = EmailPathsWithTests.Keys.Where(k => !actual.Contains(k)).ToList();

        stale.Should().BeEmpty(
            "these are listed as email paths but no longer take an IEmailSender — " +
            "remove them, so the list keeps meaning what it says");
    }

    [Fact]
    public void No_shipped_settings_file_names_the_unverified_sender()
    {
        // The same address came back through configuration. appsettings.Production.json
        // still named no-reply@pharmacyposter.app; production read it the day it
        // switched from Development to Production, and every email was accepted
        // by the API and then never arrived. The sender belongs in App Service
        // settings (Messaging__Mailjet__FromAddress) or appsettings.json — never
        // an address Mailjet has not verified.
        var web = Path.Combine(SourceRoot(), "src", "Acme.WebApi");
        var offenders = Directory.EnumerateFiles(web, "appsettings*.json")
            .Where(f => File.ReadAllText(f).Contains("@pharmacyposter.app", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("pharmacyposter.app is not a sender Mailjet has verified");
    }

    [Fact]
    public void The_invitation_email_does_not_name_a_sender()
    {
        // The specific regression, pinned by name. The scan above would catch
        // it, but this says which email and why when it fails.
        var path = Path.Combine(
            SourceRoot(), "src", "Acme.Application", "Invitations", "InvitationEmail.cs");

        File.Exists(path).Should().BeTrue("InvitationEmail.cs moved — update this test");

        // Reads the constant's value, not the file's text.
        //
        // The first version searched the whole file for "pharmacyposter.app"
        // and failed the moment the fix was documented — the comment above the
        // constant names the bad address in order to explain why the constant
        // is empty. A test that cannot tell code from prose forbids writing
        // down what happened, which is the opposite of useful.
        var match = Regex.Match(
            File.ReadAllText(path),
            @"const\s+string\s+FromAddress\s*=\s*""(?<value>[^""]*)""");

        match.Success.Should().BeTrue(
            "InvitationEmail must declare a FromAddress constant for this test to check");

        match.Groups["value"].Value.Should().BeEmpty(
            "a value here overrides the configured, provider-verified sender. It was " +
            "no-reply@pharmacyposter.app, an unverified domain, so every invitation was " +
            "refused at Mailjet while the code looked correct");
    }
}
