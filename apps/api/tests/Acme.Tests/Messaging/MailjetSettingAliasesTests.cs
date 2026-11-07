using Acme.Infrastructure.Messaging;
using Acme.Infrastructure.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Acme.Tests.Messaging;

/// <summary>
/// Mailjet keys stored under another common name still work, and the canonical
/// name always wins. From production: keys visible in the App Service settings,
/// "Mailjet credentials missing" in every send.
/// </summary>
public class MailjetSettingAliasesTests
{
    private static (MessagingOptions Options, IReadOnlyList<string> Used) Resolve(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new MessagingOptions();
        config.GetSection(MessagingOptions.SectionName).Bind(options);
        return (options, MailjetSettingAliases.Apply(options, config));
    }

    [Fact]
    public void Keys_under_the_names_the_azure_scripts_use_are_picked_up()
    {
        var (o, used) = Resolve(new() { ["MAILJET_API_KEY"] = "pub", ["MAILJET_SECRET_KEY"] = "priv" });

        o.Mailjet.ApiKey.Should().Be("pub");
        o.Mailjet.SecretKey.Should().Be("priv");
        used.Should().BeEquivalentTo("MAILJET_API_KEY", "MAILJET_SECRET_KEY");
    }

    [Fact]
    public void Keys_under_a_section_without_Messaging_are_picked_up() =>
        Resolve(new() { ["Mailjet:ApiKey"] = "pub", ["Mailjet:SecretKey"] = "priv" })
            .Options.Mailjet.SecretKey.Should().Be("priv");

    [Fact]
    public void The_canonical_name_always_wins()
    {
        var (o, used) = Resolve(new()
        {
            ["Messaging:Mailjet:ApiKey"] = "canonical",
            ["Messaging:Mailjet:SecretKey"] = "canonical-secret",
            ["MAILJET_API_KEY"] = "alias",
            ["MAILJET_SECRET_KEY"] = "alias-secret",
        });

        o.Mailjet.ApiKey.Should().Be("canonical");
        o.Mailjet.SecretKey.Should().Be("canonical-secret");
        used.Should().BeEmpty();
    }

    [Fact]
    public void Nothing_anywhere_stays_missing()
    {
        var (o, used) = Resolve(new());

        o.Mailjet.ApiKey.Should().BeNullOrEmpty();
        used.Should().BeEmpty();
    }

    [Fact]
    public void Names_are_reported_as_environment_variables() =>
        MailjetSettingAliases.EnvName("Mailjet:ApiKey").Should().Be("Mailjet__ApiKey");
}
