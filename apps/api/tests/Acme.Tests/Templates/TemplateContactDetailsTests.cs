using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Domain.Templates;
using Acme.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Templates;

/// <summary>
/// The built-in templates must not invent a pharmacy's contact details.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Twenty of the twenty-four built-in templates
/// shipped with a contact line typed into the design — <c>555-0102</c>,
/// <c>acmepharmacy.com</c>, <c>123 Main Street, Anytown</c> — and not one used
/// the <c>{{address}}</c> tokens that <c>applyPharmacyInfo</c> fills. So
/// pressing <b>Apply pharmacy info</b> found no tokens, took its "this template
/// says nothing about the pharmacy" branch, and appended a second contact line.
/// </para>
/// <para>
/// It surfaced as an address printed twice in a campaign that had already been
/// sent. The quieter half is that a pharmacist who did not spot the duplicate
/// sent patients a phone number belonging to nobody.
/// </para>
/// <para>
/// <c>check-brand.mjs</c> should have caught <c>acmepharmacy.com</c> and did
/// not: it matches <c>/\bAcme\b/</c>, which is case-sensitive and word-bounded,
/// so lowercase <c>acmepharmacy</c> fails it twice over. That is tightened
/// separately; this asserts the property directly against the templates.
/// </para>
/// </remarks>
public class TemplateContactDetailsTests
{
    /// <summary>
    /// Invented values, and anything shaped like a real contact detail.
    /// </summary>
    private static readonly (Regex Pattern, string Why)[] Forbidden =
    [
        (new Regex(@"555-0\d{3}"),
            "an invented phone number a pharmacist may not notice and patients will dial"),
        (new Regex(@"acmepharmacy", RegexOptions.IgnoreCase),
            "a website that belongs to nobody"),
        (new Regex(@"marketing25\.co\.uk", RegexOptions.IgnoreCase),
            "our own domain on a pharmacy's poster"),
        (new Regex(@"\d+\s+Main Street|Anytown", RegexOptions.IgnoreCase),
            "an invented postal address"),
        (new Regex(@"\bYour Pharmacy\b", RegexOptions.IgnoreCase),
            "a placeholder name where {{pharmacy_name}} belongs"),
    ];

    private static IEnumerable<(string Id, string Name, string Canvas)> Templates()
    {
        foreach (var t in TemplateSeeder.DefaultTemplatesForTesting())
            yield return (t.Id, t.Name, t.CanvasJson);
    }

    [Fact]
    public void No_template_invents_contact_details()
    {
        var offences = new List<string>();

        foreach (var (id, name, canvas) in Templates())
        {
            foreach (var (pattern, why) in Forbidden)
            {
                var m = pattern.Match(canvas);
                if (m.Success)
                    offences.Add($"{id} ({name}): \"{m.Value}\" — {why}");
            }
        }

        offences.Should().BeEmpty(
            "a built-in template must leave contact details to the pharmacy. Use "
            + "{{pharmacy_name}}, {{address}} and {{phone}}, which Apply pharmacy "
            + "info fills in place:\n  " + string.Join("\n  ", offences));
    }

    [Fact]
    public void No_template_shows_the_address_twice()
    {
        // The fault as the pharmacist met it. Two {{address}} tokens in one
        // design produce two real addresses once the details are applied, which
        // is the same email they complained about with the placeholders
        // swapped out.
        var offences = new List<string>();

        foreach (var (id, name, canvas) in Templates())
        {
            var count = Regex.Matches(canvas, @"\{\{\s*address\s*\}\}").Count;
            if (count > 1)
                offences.Add($"{id} ({name}): {count} address tokens");
        }

        offences.Should().BeEmpty(
            "one design, one address:\n  " + string.Join("\n  ", offences));
    }

    [Fact]
    public void Every_canvas_is_still_valid_json()
    {
        // The tokens were introduced by rewriting strings inside C# raw string
        // literals. A stray quote there takes out the whole gallery, and the
        // symptom is templates simply not appearing rather than an error.
        foreach (var (id, name, canvas) in Templates())
        {
            var parse = () => JsonDocument.Parse(canvas);
            parse.Should().NotThrow($"template {id} ({name}) must be loadable");
        }
    }

    [Fact]
    public void A_template_that_names_the_pharmacy_also_gives_its_address_somewhere()
    {
        // Not a rule about every template — a poster is allowed to carry no
        // contact block at all, and five of them do. This catches the halfway
        // state: a design that was converted to tokens for the name and phone
        // but left an address behind as typed text.
        foreach (var (id, name, canvas) in Templates())
        {
            var hasNameToken = Regex.IsMatch(canvas, @"\{\{\s*pharmacy(_| )?name\s*\}\}");
            if (!hasNameToken) continue;

            var looksLikeTypedAddress = Regex.IsMatch(
                canvas, @"""text"":""[^""]*\b\d+\s+[A-Z][a-z]+\s+(Street|Road|Lane|Avenue)");

            looksLikeTypedAddress.Should().BeFalse(
                $"{id} ({name}) uses tokens for the name but appears to have a typed "
                + "address as well, which Apply pharmacy info cannot update");
        }
    }
}
