using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Domain.Patients;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Patients;

/// <summary>
/// Reading and writing a contact list as CSV.
/// </summary>
/// <remarks>
/// The cases here are the ones real files actually contain: a byte-order mark
/// from Excel, a quoted address with a comma in it, the same person twice,
/// a header nobody renamed. Getting those right is most of what an importer is.
/// </remarks>
public class PatientCsvTests
{
    private const string Header = "Full Name,Email,Phone Number,Notes";

    [Fact]
    public void A_plain_file_parses()
    {
        var parsed = PatientCsv.Parse(
            $"{Header}\nJane Smith,jane@example.com,07700 900123,Prefers text\n");

        parsed.Rows.Should().ContainSingle();
        parsed.Issues.Should().BeEmpty();

        var row = parsed.Rows[0];
        row.FullName.Should().Be("Jane Smith");
        row.Email.Should().Be("jane@example.com");
        row.LineNumber.Should().Be(2, "line 1 is the header — the operator counts from the file");
    }

    [Fact]
    public void A_byte_order_mark_does_not_break_the_header()
    {
        // Excel writes one by default. Without stripping it the first column
        // reads as "﻿Full Name", matches nothing, and the importer reports
        // "the header needs a name column" on a file that plainly has one.
        var parsed = PatientCsv.Parse(
            "﻿" + Header + "\nJane Smith,jane@example.com,,\n");

        parsed.Rows.Should().ContainSingle("the BOM must not hide the name column");
    }

    [Fact]
    public void Quoted_fields_containing_commas_survive()
    {
        var parsed = PatientCsv.Parse(
            $"{Header}\n\"Smith, Jane\",jane@example.com,,\"Flu clinic, second dose\"\n");

        parsed.Rows.Should().ContainSingle();
        parsed.Rows[0].FullName.Should().Be("Smith, Jane");
        parsed.Rows[0].Notes.Should().Be("Flu clinic, second dose");
    }

    [Theory]
    [InlineData("Name,E-mail,Mobile")]
    [InlineData("Patient Name,Email Address,Telephone")]
    [InlineData("full name,email,phone")]
    public void Common_header_spellings_are_accepted(string header)
    {
        // An export from a till system is not going to use our column names,
        // and asking a pharmacist to rename columns before importing is how a
        // feature goes unused.
        var parsed = PatientCsv.Parse($"{header}\nJane Smith,jane@example.com,07700900123\n");

        parsed.Rows.Should().ContainSingle("'{0}' should be understood", header);
    }

    [Fact]
    public void A_row_with_no_way_to_contact_them_is_reported()
    {
        var parsed = PatientCsv.Parse($"{Header}\nJane Smith,,,\n");

        parsed.Rows.Should().BeEmpty();
        parsed.Issues.Should().ContainSingle()
            .Which.Reason.Should().Contain("no way to contact");
    }

    [Fact]
    public void A_bad_email_is_reported_with_its_line_number()
    {
        var parsed = PatientCsv.Parse(
            $"{Header}\nJane Smith,jane@example.com,,\nBad Row,not-an-email,,\n");

        parsed.Rows.Should().ContainSingle("the good row still imports");
        parsed.Issues.Should().ContainSingle()
            .Which.LineNumber.Should().Be(3);
    }

    [Fact]
    public void The_same_contact_twice_in_one_file_is_reported_once()
    {
        // A copy-paste mistake, and worth separating from a clash with a
        // contact already saved — the fix is different.
        var parsed = PatientCsv.Parse(
            $"{Header}\nJane Smith,jane@example.com,,\nJane S,JANE@EXAMPLE.COM,,\n");

        parsed.Rows.Should().ContainSingle("case must not make it a different person");
        parsed.Issues.Should().ContainSingle()
            .Which.Reason.Should().Contain("appears earlier");
    }

    [Fact]
    public void A_header_with_no_name_column_is_refused_as_a_file()
    {
        var parsed = PatientCsv.Parse("Email,Phone\njane@example.com,07700900123\n");

        parsed.Rows.Should().BeEmpty();
        parsed.Issues.Should().ContainSingle()
            .Which.Reason.Should().Contain("name column");
    }

    [Fact]
    public void A_header_with_neither_email_nor_phone_is_refused()
    {
        var parsed = PatientCsv.Parse("Full Name,Notes\nJane Smith,Prefers text\n");

        parsed.Issues.Should().ContainSingle()
            .Which.Reason.Should().Contain("email or a phone");
    }

    [Fact]
    public void Blank_lines_are_not_counted_as_rows()
    {
        var parsed = PatientCsv.Parse(
            $"{Header}\nJane Smith,jane@example.com,,\n\n\n");

        parsed.DataRows.Should().Be(1, "trailing newlines are not failed rows");
        parsed.Issues.Should().BeEmpty();
    }

    /* ── writing ─────────────────────────────────────────────────────── */

    [Fact]
    public void An_export_can_be_imported_straight_back()
    {
        // The most common reason to export is to edit and re-upload, so the
        // round trip has to work without touching the columns.
        var patients = new[]
        {
            Patient.Create("owner", "Smith, Jane", "jane@example.com", "07700900123", "Prefers \"text\""),
        };

        var parsed = PatientCsv.Parse(PatientCsv.Write(patients));

        parsed.Issues.Should().BeEmpty();
        parsed.Rows.Should().ContainSingle();
        parsed.Rows[0].FullName.Should().Be("Smith, Jane");
        parsed.Rows[0].Notes.Should().Be("Prefers \"text\"");
    }

    [Fact]
    public void A_contact_name_cannot_smuggle_a_formula_into_the_export()
    {
        // Formula injection. A cell beginning =, +, - or @ is executed when the
        // file is opened in Excel, so a contact named =HYPERLINK(...) becomes a
        // live link in the spreadsheet of whoever exports the list. Every value
        // here came from user input, which is exactly what the attack needs.
        var patients = new[]
        {
            Patient.Create("owner", "=HYPERLINK(\"http://evil\",\"click\")", "x@example.com", null, ""),
        };

        var csv = PatientCsv.Write(patients);

        csv.Should().NotContain("\n=", "a data cell must not start a formula");
        csv.Should().NotContain(",=", "nor after a delimiter");
        csv.Should().Contain("\t=", "the value is preserved, prefixed so Excel treats it as text");
    }

    [Theory]
    [InlineData("=cmd")]
    [InlineData("+44 7700 900123")]
    [InlineData("-1")]
    [InlineData("@example")]
    [InlineData("\t=cmd")]
    public void Every_formula_prefix_is_neutralised(string value)
    {
        Csv.Escape(value).Should().StartWith("\t", "'{0}' would otherwise be evaluated", value);
    }
}
