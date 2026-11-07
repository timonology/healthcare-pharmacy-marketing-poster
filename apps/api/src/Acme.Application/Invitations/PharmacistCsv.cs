using System.Text;
using Acme.Application.Common;

namespace Acme.Application.Invitations;

/// <summary>
/// Parses an uploaded pharmacist list.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a CSV library, because the format this accepts is
/// deliberately narrow and the error messages matter more than the parsing
/// does. Someone exporting from a spreadsheet needs to be told which line is
/// wrong and why, not handed a parser exception.
/// </para>
/// <para>
/// Handles quoted fields and embedded commas, which is the one piece of real
/// CSV complexity that turns up immediately — addresses contain commas.
/// </para>
/// </remarks>
public static class PharmacistCsv
{
    /// <summary>
    /// Column aliases, so an export does not have to be renamed by hand.
    /// </summary>
    /// <remarks>
    /// ODS code and F-code are the same thing in this domain, and different
    /// systems export it under different names. Accepting both spellings costs
    /// one dictionary entry and saves every user a manual edit.
    /// </remarks>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["full name"] = "name", ["fullname"] = "name", ["name"] = "name",
        ["contact"] = "name", ["contact name"] = "name", ["pharmacist"] = "name",

        ["email"] = "email", ["email address"] = "email", ["e-mail"] = "email",

        ["ods"] = "fcode", ["ods code"] = "fcode", ["odscode"] = "fcode",
        ["fcode"] = "fcode", ["f code"] = "fcode", ["f-code"] = "fcode",

        ["address"] = "address", ["address line 1"] = "address", ["street"] = "address",

        ["postcode"] = "postcode", ["post code"] = "postcode", ["zip"] = "postcode",

        ["phone"] = "phone", ["phone number"] = "phone", ["telephone"] = "phone",
        ["tel"] = "phone", ["mobile"] = "phone",

        ["pharmacy"] = "pharmacy", ["pharmacy name"] = "pharmacy",
        ["trading name"] = "pharmacy", ["branch"] = "pharmacy",
    };

    /// <summary>The header this produces when a user asks for a template.</summary>
    public const string TemplateHeader =
        "Full Name,Email,ODS Code,Pharmacy Name,Address,Postcode,Phone Number";

    public static string TemplateFile() =>
        TemplateHeader + "\n"
        + "Jane Smith,jane@example-pharmacy.co.uk,FA123,Smith's Pharmacy,"
        + "\"12 High Street, Aylesbury\",HP20 1AB,01296 123456\n";

    /// <summary>
    /// Parses the file, separating usable rows from ones that need attention.
    /// </summary>
    /// <remarks>
    /// Nothing is created here. The caller shows this to the SuperAdmin first,
    /// because a 200-row import is not something to run and then discover was
    /// pointed at the wrong column.
    /// </remarks>
    public static CsvPreviewDto Parse(string content)
    {
        var valid = new List<InvitePharmacistRequest>();
        var errors = new List<CsvRowError>();

        var lines = Csv.SplitLines(content);
        if (lines.Count == 0)
            return new CsvPreviewDto(valid, new[] { new CsvRowError(0, "The file is empty.", null) }, 0);

        var header = Csv.ParseLine(lines[0]);
        var columns = Csv.MapHeader(header, Aliases);

        if (!columns.ContainsKey("name") || !columns.ContainsKey("email"))
            return new CsvPreviewDto(
                valid,
                new[]
                {
                    new CsvRowError(1,
                        "The header must include a name column and an email column. Expected: "
                        + TemplateHeader, null),
                },
                0);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataRows = 0;

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            dataRows++;
            var lineNumber = i + 1;
            var cells = Csv.ParseLine(line);

            string? Get(string key) =>
                columns.TryGetValue(key, out var index) && index < cells.Count
                    ? NullIfBlank(cells[index])
                    : null;

            var name = Get("name");
            var mail = Get("email");

            if (name is null)
            {
                errors.Add(new CsvRowError(lineNumber, "No name in this row.", mail));
                continue;
            }

            if (mail is null)
            {
                errors.Add(new CsvRowError(lineNumber, "No email address in this row.", null));
                continue;
            }

            // Shape check only. The domain's Email type does the real
            // validation, and duplicating its rules here would give two
            // definitions of a valid address that could disagree.
            if (!mail.Contains('@') || mail.StartsWith('@') || mail.EndsWith('@'))
            {
                errors.Add(new CsvRowError(lineNumber, $"'{mail}' is not an email address.", mail));
                continue;
            }

            // Caught here rather than at insert time so the operator sees it in
            // the preview. A duplicate inside one file is a copy-paste mistake,
            // and the second row would otherwise silently become a resend.
            if (!seen.Add(mail))
            {
                errors.Add(new CsvRowError(lineNumber, "This email appears earlier in the file.", mail));
                continue;
            }

            valid.Add(new InvitePharmacistRequest(
                FullName: name,
                Email: mail,
                FCode: Get("fcode"),
                Address: Get("address"),
                PostCode: Get("postcode"),
                Phone: Get("phone"),
                PharmacyName: Get("pharmacy")));
        }

        return new CsvPreviewDto(valid, errors, dataRows);
    }

    // SplitLines, ParseLine and the header mapping now live in Csv, shared with
    // the patient importer. They were private here first; a second copy would
    // have been two parsers drifting apart on the details nobody reimplements
    // correctly — the byte-order mark, the doubled quote.

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
