using System.Text;
using Acme.Application.Common;
using Acme.Domain.Patients;

namespace Acme.Application.Patients;

/// <summary>One parsed row, before anything has been created.</summary>
public sealed record PatientCsvRow(
    int LineNumber,
    string FullName,
    string? Email,
    string? Phone,
    string? Notes,
    // Security review R2-06: "yes" in a consent column records consent; blank
    // or "no" records none. Null when the file has no such column.
    bool? MarketingConsent = null);

/// <summary>What happened to a row that did not become a patient.</summary>
public sealed record PatientCsvIssue(int LineNumber, string Reason, string? Value);

/// <summary>The result of reading a file, before touching the database.</summary>
public sealed record PatientCsvParse(
    IReadOnlyList<PatientCsvRow> Rows,
    IReadOnlyList<PatientCsvIssue> Issues,
    int DataRows);

/// <summary>
/// Reading and writing a pharmacy's marketing contacts as CSV.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no clinical column and there will not be one.</b> These are
/// marketing contacts — a name and a way to reach them. A diagnosis or
/// medication column would turn a marketing list into a health record, which
/// changes what the product is, what it must be certified for, and what a
/// breach would mean. The notes field exists for things like "prefers text",
/// and the import copy says so.
/// </para>
/// <para>
/// Validation is not reimplemented here. Rows are handed to
/// <see cref="Patient.Create"/>, which already normalises an email to
/// lowercase, strips a phone to digits and <c>+</c>, and refuses a contact
/// with neither. Duplicating those rules would mean an import accepting
/// something the rest of the app rejects.
/// </para>
/// </remarks>
public static class PatientCsv
{
    /// <summary>
    /// Column aliases, so an export from another system needs no hand-editing.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["full name"] = "name", ["fullname"] = "name", ["name"] = "name",
        ["patient"] = "name", ["patient name"] = "name", ["contact"] = "name",
        ["first name"] = "name",

        ["email"] = "email", ["email address"] = "email", ["e-mail"] = "email",

        ["phone"] = "phone", ["phone number"] = "phone", ["telephone"] = "phone",
        ["tel"] = "phone", ["mobile"] = "phone", ["mobile number"] = "phone",

        ["notes"] = "notes", ["note"] = "notes", ["comment"] = "notes",
        ["comments"] = "notes", ["preferences"] = "notes",

        ["marketing consent"] = "consent", ["consent"] = "consent", ["email consent"] = "consent",
        ["opted in"] = "consent", ["opt in"] = "consent", ["opt-in"] = "consent",
    };

    public const string TemplateHeader = "Full Name,Email,Phone Number,Notes,Marketing Consent";

    public static string TemplateFile() =>
        TemplateHeader + "\n"
        + "Jane Smith,jane@example.com,07700 900123,Prefers text messages,Yes\n"
        + "Mohammed Ali,mo@example.com,,,No\n"
        + "Sara Okonkwo,,07700 900456,Flu clinic reminder list,\n";

    /// <summary>Yes/no in a consent cell; null when blank; false with an error when unreadable.</summary>
    private static bool? ParseConsent(string? cell, out bool unreadable)
    {
        unreadable = false;
        if (string.IsNullOrWhiteSpace(cell)) return null;
        switch (cell.Trim().ToLowerInvariant())
        {
            case "yes": case "y": case "true": case "1": case "opted in": case "x":
                return true;
            case "no": case "n": case "false": case "0": case "opted out":
                return false;
            default:
                unreadable = true;
                return null;
        }
    }

    /// <summary>
    /// Reads the file, separating usable rows from ones needing attention.
    /// </summary>
    /// <remarks>
    /// Creates nothing. The caller decides what to do with the result, which is
    /// what makes a preview possible — a 500-row import is not something to run
    /// and then discover was pointed at the wrong column.
    /// </remarks>
    public static PatientCsvParse Parse(string content)
    {
        var rows = new List<PatientCsvRow>();
        var issues = new List<PatientCsvIssue>();

        var lines = Csv.SplitLines(content);
        if (lines.Count == 0 || lines.All(string.IsNullOrWhiteSpace))
            return new PatientCsvParse(rows, new[] { new PatientCsvIssue(0, "The file is empty.", null) }, 0);

        var columns = Csv.MapHeader(Csv.ParseLine(lines[0]), Aliases);

        if (!columns.ContainsKey("name"))
            return new PatientCsvParse(rows, new[]
            {
                new PatientCsvIssue(1,
                    "The header needs a name column. Expected something like: " + TemplateHeader, null),
            }, 0);

        if (!columns.ContainsKey("email") && !columns.ContainsKey("phone"))
            return new PatientCsvParse(rows, new[]
            {
                new PatientCsvIssue(1,
                    "The header needs an email or a phone column — a contact with neither "
                    + "cannot be sent anything.", null),
            }, 0);

        // Tracks contacts seen earlier in this same file. A duplicate inside one
        // file is a copy-paste mistake and worth reporting separately from one
        // that clashes with a contact already saved.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataRows = 0;

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            dataRows++;
            var lineNumber = i + 1;
            var cells = Csv.ParseLine(line);

            var name = Csv.Cell(cells, columns, "name");
            var email = Csv.Cell(cells, columns, "email");
            var phone = Csv.Cell(cells, columns, "phone");
            var notes = Csv.Cell(cells, columns, "notes");
            var consent = ParseConsent(Csv.Cell(cells, columns, "consent"), out var consentUnreadable);
            if (consentUnreadable)
            {
                // Not guessed: recording consent nobody gave is the one mistake
                // this column must not make.
                issues.Add(new PatientCsvIssue(
                    lineNumber, "Marketing consent must be Yes or No (or left blank).", name));
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(new PatientCsvIssue(lineNumber, "No name in this row.", null));
                continue;
            }

            if (email is null && phone is null)
            {
                issues.Add(new PatientCsvIssue(
                    lineNumber, "No email or phone — there would be no way to contact them.", name));
                continue;
            }

            // Validate through the domain rather than re-deriving the rules, so
            // an imported contact is exactly as valid as a typed one.
            try
            {
                _ = Patient.Create("validation-probe", name, email, phone, notes);
            }
            catch (Exception ex)
            {
                issues.Add(new PatientCsvIssue(lineNumber, ex.Message, email ?? phone));
                continue;
            }

            var key = email?.Trim().ToLowerInvariant()
                ?? new string((phone ?? "").Where(c => c == '+' || char.IsDigit(c)).ToArray());

            if (!seen.Add(key))
            {
                issues.Add(new PatientCsvIssue(
                    lineNumber, "This contact appears earlier in the file.", email ?? phone));
                continue;
            }

            rows.Add(new PatientCsvRow(lineNumber, name, email, phone, notes, consent));
        }

        return new PatientCsvParse(rows, issues, dataRows);
    }

    /// <summary>Writes a pharmacy's contacts out.</summary>
    /// <remarks>
    /// Column order matches <see cref="TemplateHeader"/> so an export can be
    /// edited and imported straight back, which is the most common reason
    /// anyone exports at all.
    /// </remarks>
    public static string Write(IEnumerable<Patient> patients)
    {
        var sb = new StringBuilder();
        sb.Append(TemplateHeader).Append('\n');

        foreach (var p in patients)
            sb.Append(Csv.Row(p.FullName, p.Email, p.Phone, p.Notes,
                p.MarketingConsentAtUtc is null ? "" : "Yes")).Append('\n');

        return sb.ToString();
    }
}
