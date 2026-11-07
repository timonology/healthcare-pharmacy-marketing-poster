using Acme.Application.Invitations;
using Xunit;

namespace Acme.Tests.Invitations;

/// <summary>
/// CSV import parsing.
/// </summary>
/// <remarks>
/// Weighted towards the shapes a real export actually produces — Excel's byte
/// order mark, CRLF endings, addresses containing commas — rather than the
/// tidy case. A parser that only handles the tidy case fails on the first file
/// anyone uploads.
/// </remarks>
public class PharmacistCsvTests
{
    [Fact]
    public void An_address_containing_a_comma_stays_one_field()
    {
        // The single most common real-world case, and the reason this is not a
        // string.Split(',').
        var result = PharmacistCsv.Parse(
            "Full Name,Email,Address\nJane Smith,j@x.co,\"12 High Street, Aylesbury\"\n");

        var row = Assert.Single(result.Valid);
        Assert.Equal("12 High Street, Aylesbury", row.Address);
    }

    [Fact]
    public void An_excel_byte_order_mark_does_not_break_the_header()
    {
        // Excel writes a BOM by default. Without stripping it the first header
        // cell reads as "﻿Full Name", matches nothing, and the file is
        // rejected for having no name column — on a file that plainly has one.
        var result = PharmacistCsv.Parse("﻿Full Name,Email\nJane,j@x.co\n");

        Assert.Single(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("ODS Code")]
    [InlineData("ODS")]
    [InlineData("F-Code")]
    [InlineData("fcode")]
    public void The_ods_column_is_accepted_under_any_of_its_names(string header)
    {
        // ODS code and F-code are the same field in this domain, and different
        // systems export it under different labels.
        var result = PharmacistCsv.Parse($"Name,Email,{header}\nJane,j@x.co,FA123\n");

        Assert.Equal("FA123", Assert.Single(result.Valid).FCode);
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        var result = PharmacistCsv.Parse("Name,Email\r\nJane,j@x.co\r\n");
        Assert.Single(result.Valid);
    }

    [Fact]
    public void A_duplicate_email_inside_the_file_is_reported_not_imported_twice()
    {
        // Caught in the preview so the operator sees it. Left alone, the second
        // row would silently become a resend of the first.
        var result = PharmacistCsv.Parse("Name,Email\nA,dup@x.co\nB,DUP@x.co\n");

        Assert.Single(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.Line);
        Assert.Contains("earlier", error.Reason);
    }

    [Fact]
    public void A_header_without_name_or_email_rejects_the_whole_file()
    {
        // A whole-file error, not per row — every row would fail identically,
        // and 200 copies of the same message helps nobody.
        var result = PharmacistCsv.Parse("Pharmacy,Postcode\nBoots,HP20 1AB\n");

        Assert.Empty(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("header", error.Reason);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@nolocal.co")]
    [InlineData("trailing@")]
    public void A_malformed_email_is_reported_with_its_line(string address)
    {
        var result = PharmacistCsv.Parse($"Name,Email\nJane,{address}\n");

        Assert.Empty(result.Valid);
        Assert.Equal(2, Assert.Single(result.Errors).Line);
    }

    [Fact]
    public void A_row_with_no_name_is_reported_against_its_email()
    {
        // The email is echoed back so the operator can find the row in their
        // spreadsheet — a line number alone is harder to act on.
        var result = PharmacistCsv.Parse("Name,Email\n,orphan@x.co\n");

        Assert.Equal("orphan@x.co", Assert.Single(result.Errors).Email);
    }

    [Fact]
    public void Blank_trailing_lines_are_not_counted_as_rows()
    {
        // Every text editor leaves one. Counting them would report "3 rows, 2
        // errors" on a file with one pharmacist.
        var result = PharmacistCsv.Parse("Name,Email\nJane,j@x.co\n\n\n");

        Assert.Single(result.Valid);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.TotalRows);
    }

    [Fact]
    public void Doubled_quotes_inside_a_quoted_field_are_unescaped()
    {
        var result = PharmacistCsv.Parse(
            "Name,Email,Pharmacy\nJane,j@x.co,\"O\"\"Brien Pharmacy\"\n");

        Assert.Equal("O\"Brien Pharmacy", Assert.Single(result.Valid).PharmacyName);
    }

    [Fact]
    public void A_short_row_leaves_missing_columns_null_rather_than_failing()
    {
        // Trailing empty columns are routinely omitted by exports.
        var result = PharmacistCsv.Parse("Name,Email,Phone\nJane,j@x.co\n");

        var row = Assert.Single(result.Valid);
        Assert.Null(row.Phone);
    }

    [Fact]
    public void The_template_parses_as_its_own_valid_input()
    {
        // Guards the pair: an example row that the parser rejects would send
        // someone in circles, and it is the first thing anyone tries.
        var result = PharmacistCsv.Parse(PharmacistCsv.TemplateFile());

        Assert.Single(result.Valid);
        Assert.Empty(result.Errors);
    }
}
