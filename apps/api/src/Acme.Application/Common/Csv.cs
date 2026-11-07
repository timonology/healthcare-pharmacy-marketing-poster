using System.Text;

namespace Acme.Application.Common;

/// <summary>
/// The tedious parts of reading and writing CSV, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>PharmacistCsv</c>, which had all of this as private
/// methods, when patients needed the same behaviour. Copying it would have
/// meant two parsers drifting apart — and the parts worth having are precisely
/// the ones nobody remembers to reimplement: the byte-order mark, the doubled
/// quote, the trailing newline.
/// </para>
/// <para>
/// Deliberately not a CSV library. The files this reads are exports from
/// Excel and other pharmacy systems, and the subset of the format they use is
/// small. A dependency would buy edge cases none of them produce.
/// </para>
/// </remarks>
public static class Csv
{
    /// <summary>Splits on newlines, tolerating CRLF and a UTF-8 byte-order mark.</summary>
    /// <remarks>
    /// The BOM matters more than it looks. Excel writes one by default, and
    /// without stripping it the first header cell reads as "﻿Full Name" and
    /// matches nothing — producing "the header must include a name column" on
    /// a file that plainly has one.
    /// </remarks>
    public static List<string> SplitLines(string content)
    {
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];

        return content
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
    }

    /// <summary>Splits one line, honouring quotes and doubled quote escapes.</summary>
    public static List<string> ParseLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" inside a quoted field is a literal quote.
                    if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': cells.Add(cell.ToString()); cell.Clear(); break;
                default: cell.Append(c); break;
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    /// <summary>
    /// Maps canonical column names to their index, using an alias table.
    /// </summary>
    /// <remarks>
    /// Aliases exist so an export from another system does not have to be
    /// renamed by hand before it can be used. First match wins, so a file with
    /// both "Email" and "Email Address" resolves to the leftmost.
    /// </remarks>
    public static Dictionary<string, int> MapHeader(
        IReadOnlyList<string> header, IReadOnlyDictionary<string, string> aliases)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < header.Count; i++)
        {
            var key = header[i].Trim().Trim('"');
            if (aliases.TryGetValue(key, out var canonical) && !map.ContainsKey(canonical))
                map[canonical] = i;
        }

        return map;
    }

    /// <summary>Reads a mapped cell, or null when the column is absent or blank.</summary>
    public static string? Cell(
        IReadOnlyList<string> cells, IReadOnlyDictionary<string, int> map, string column)
    {
        if (!map.TryGetValue(column, out var index)) return null;
        if (index >= cells.Count) return null;

        var value = cells[index].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Escapes one value for writing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quotes anything containing a comma, quote or newline, and doubles inner
    /// quotes — the inverse of <see cref="ParseLine"/>.
    /// </para>
    /// <para>
    /// <b>Also neutralises formula injection.</b> A cell beginning <c>=</c>,
    /// <c>+</c>, <c>-</c> or <c>@</c> is executed as a formula when the file is
    /// opened in Excel, so a contact named <c>=HYPERLINK(...)</c> becomes a
    /// live link in the spreadsheet of whoever exports the list. Prefixing a
    /// tab stops that while still displaying the original text. Every value
    /// here came from user input, which is exactly the situation the attack
    /// needs.
    /// </para>
    /// </remarks>
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        // Tab and carriage return too: a cell starting with either is also
        // read as a formula by some spreadsheet programs (OWASP CSV injection),
        // and the audit export already treats them so (security review R3-07).
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            text = "\t" + text;

        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            return "\"" + text.Replace("\"", "\"\"") + "\"";

        return text;
    }

    /// <summary>Joins one row, escaping each cell.</summary>
    public static string Row(params string?[] cells) =>
        string.Join(",", cells.Select(Escape));
}
