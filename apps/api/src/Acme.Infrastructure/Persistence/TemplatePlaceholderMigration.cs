using Acme.Application.Common;
using Acme.Application.Templates;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Replaces the invented contact details that shipped inside the built-in
/// templates with the tokens that pull a pharmacy's real ones.
/// </summary>
/// <remarks>
/// <para>
/// <b>What went wrong.</b> Twenty of the twenty-four built-in templates carried
/// a contact line typed straight into the design — <c>555-0102</c>,
/// <c>acmepharmacy.com</c>, <c>123 Main Street, Anytown</c> — and none of them
/// used the <c>{{address}}</c> style tokens that <c>applyPharmacyInfo</c> exists
/// to fill. Pressing <b>Apply pharmacy info</b> therefore found nothing to
/// replace, fell through to its "this template says nothing about the pharmacy"
/// branch, and appended a second contact line. The poster then showed the
/// address twice, which is how this was found: in a campaign a pharmacy had
/// already sent.
/// </para>
/// <para>
/// The worse half is the half nobody reported. A pharmacist who did not notice
/// the duplication sent patients a poster carrying a working-looking phone
/// number that belongs to nobody, and a website that is not theirs.
/// </para>
/// <para>
/// <b>Why a migration is needed at all.</b> <see cref="TemplateSeeder"/> only
/// inserts templates that are missing by id; it will not touch one that already
/// exists, deliberately, so that an admin's edits are not reverted on every
/// deploy. Fixing the seeder therefore reaches new environments only. Every
/// database that has already seeded — development, test, production — keeps the
/// old text until something rewrites it.
/// </para>
/// <para>
/// <b>What it will not touch.</b> Only templates whose canvas still contains one
/// of the placeholder strings below. A template an admin has edited has almost
/// certainly lost them, and is left alone. It is keyed on the invented values
/// rather than on the template id for that reason: the id says which template
/// it is, the placeholder says whether anyone has been at it since.
/// </para>
/// <para>
/// Safe to run repeatedly. After the first pass no canvas contains a
/// placeholder, so every later run is a read and nothing more.
/// </para>
/// </remarks>
public sealed class TemplatePlaceholderMigration(
    ITemplateRepository templates,
    ILogger<TemplatePlaceholderMigration> log) : IStartupTask
{
    /// <summary>
    /// Longest first. "Your Pharmacy" is a substring of two longer lines, and
    /// replacing it first would leave "{{pharmacy_name}} · 555-0102 · …"
    /// behind — a half-fixed line that still shows an invented phone number.
    /// </summary>
    private static readonly (string From, string To)[] Replacements =
    [
        ("Walk in, call 555-0102, or visit acmepharmacy.com/stop-smoking",
                                                  "Walk in or call {{phone}}"),
        ("Scan the QR code or call 555-0102 to reserve a slot",
                                                  "Scan the QR code or call {{phone}} to reserve a slot"),
        ("Your Pharmacy  •  555-0102  •  marketing25.co.uk",
                                                  "{{pharmacy_name}}  •  {{phone}}  •  {{address}}"),
        ("acmepharmacy.com  •  Mon–Sat 9am – 7pm", "{{address}}"),
        ("Your community pharmacy since 1987",     "Your community pharmacy"),
        ("Welcome to Your Pharmacy",               "Welcome to {{pharmacy_name}}"),
        ("Book online or call 555-0102",           "Book online or call {{phone}}"),
        ("123 Main Street, Anytown",               "{{address}}"),
        ("acmepharmacy.com/travel",                "{{address}}"),
        ("555-0102  •  acmepharmacy.com",     "{{phone}}  •  {{address}}"),
        ("555-0123  •  acmepharmacy.com",     "{{phone}}"),
        ("YOUR PHARMACY",                          "{{pharmacy_name}}"),
        ("Your Pharmacy",                          "{{pharmacy_name}}"),
    ];

    public async Task ExecuteAsync(CancellationToken ct)
    {
        // Both arguments are load-bearing. TemplateQuery defaults to
        // IsPublished: true and Take: 50, so the plain constructor would quietly
        // skip every unpublished template and stop at fifty — leaving invented
        // phone numbers in exactly the templates nobody is looking at.
        var all = await templates.ListAsync(
            new TemplateQuery(IsPublished: null, Take: 1000), ct);

        var changed = 0;
        foreach (var template in all)
        {
            var canvas = template.CanvasJson;
            if (string.IsNullOrEmpty(canvas)) continue;

            var updated = canvas;
            foreach (var (from, to) in Replacements)
                updated = updated.Replace(from, to, StringComparison.Ordinal);

            if (updated == canvas) continue;

            // A template that carries its address in two places would reproduce
            // the original fault after the tokens are filled. Only the Welcome
            // design did, and it is corrected in the seeder; this is the guard
            // for a database where somebody has since copied that block around.
            updated = RemoveDuplicateAddressToken(updated);

            template.Update(
                template.Name, template.Description, template.Category,
                template.Tags, updated);

            await templates.UpdateAsync(template, ct);
            changed++;

            log.LogInformation(
                "Template {TemplateId} ({Name}): invented contact details replaced with tokens",
                template.Id, template.Name);
        }

        if (changed > 0)
        {
            log.LogWarning(
                "Rewrote {Count} template(s) that shipped with invented contact details. "
                + "Posters already created from them keep their own copy and are not "
                + "affected — those need the duplicate line deleting by hand.",
                changed);
        }
    }

    /// <summary>
    /// Leaves the first <c>{{address}}</c> and drops any later one.
    /// </summary>
    private static string RemoveDuplicateAddressToken(string canvas)
    {
        const string token = "{{address}}";

        var first = canvas.IndexOf(token, StringComparison.Ordinal);
        if (first < 0) return canvas;

        var next = canvas.IndexOf(token, first + token.Length, StringComparison.Ordinal);
        if (next < 0) return canvas;

        // Rebuilt rather than Replace'd from an index, because Replace has no
        // "skip the first" and removing the wrong one moves the address out of
        // the block the designer put it in.
        var tail = canvas[(first + token.Length)..].Replace(token, "", StringComparison.Ordinal);
        return canvas[..(first + token.Length)] + tail;
    }
}
