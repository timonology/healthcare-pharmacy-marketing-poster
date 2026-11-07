using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Importing and exporting a pharmacy's contacts.
/// </summary>
/// <remarks>
/// <para>
/// The scoping tests are the point. A contact list is the most sensitive thing
/// a pharmacy keeps here, and an importer is the easiest place to get scoping
/// wrong — it is the one write path that takes a whole file of other people's
/// details at once.
/// </para>
/// <para>
/// The pharmacy-facing endpoint takes no target parameter at all, so these
/// prove the property holds rather than that a check works: there is no request
/// that could reach another pharmacy's list.
/// </para>
/// </remarks>
public sealed class PatientImportTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Header = "Full Name,Email,Phone Number,Notes";

    private static MultipartFormDataContent File(string csv)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");

        // The field name must be "file" — it binds to the IFormFile parameter.
        return new MultipartFormDataContent { { content, "file", "contacts.csv" } };
    }

    private static async Task<JsonElement> ImportAsync(HttpClient client, string csv)
    {
        using var res = await client.PostAsync("/api/patients/import", File(csv));
        res.IsSuccessStatusCode.Should().BeTrue(
            "the import must succeed: {0}", await res.Content.ReadAsStringAsync());

        return await res.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static async Task<int> CountAsync(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/api/patients?take=200", Json);
        return page.GetProperty("total").GetInt32();
    }

    /* ── importing ───────────────────────────────────────────────────── */

    [Fact]
    public async Task A_csv_becomes_contacts()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("import");

        var report = await ImportAsync(client,
            $"{Header}\nJane Smith,jane-{Guid.NewGuid():N}@example.test,,\n");

        report.GetProperty("created").GetInt32().Should().Be(1);
        (await CountAsync(client)).Should().Be(1);
    }

    [Fact]
    public async Task The_report_names_every_row_that_did_not_make_it()
    {
        // "412 of 500 imported" is useless to somebody holding a messy
        // spreadsheet. Each rejected row must carry a line number and a reason.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("report");
        var good = $"ok-{Guid.NewGuid():N}@example.test";

        var report = await ImportAsync(client,
            $"{Header}\n"
            + $"Good Row,{good},,\n"
            + "No Contact,,,\n"
            + "Bad Email,not-an-email,,\n");

        report.GetProperty("created").GetInt32().Should().Be(1);
        report.GetProperty("failed").GetInt32().Should().Be(2);

        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(3, "every row is accounted for, not just the failures");

        var failures = rows.Where(r => r.GetProperty("outcome").GetString() != "created").ToList();
        failures.Should().OnlyContain(
            r => r.GetProperty("lineNumber").GetInt32() > 1
              && !string.IsNullOrWhiteSpace(r.GetProperty("detail").GetString()),
            "a rejected row needs a line number and a reason a person can act on");
    }

    [Fact]
    public async Task Importing_the_same_list_twice_adds_nothing_the_second_time()
    {
        // Re-uploading is the normal way somebody adds the few contacts they
        // missed. It must not double every contact they already had.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("twice");
        var csv = $"{Header}\nJane Smith,twice-{Guid.NewGuid():N}@example.test,,\n";

        var first = await ImportAsync(client, csv);
        first.GetProperty("created").GetInt32().Should().Be(1);

        var second = await ImportAsync(client, csv);
        second.GetProperty("created").GetInt32().Should().Be(0);
        second.GetProperty("skipped").GetInt32().Should().Be(1);

        (await CountAsync(client)).Should().Be(1, "the contact must not be duplicated");
    }

    [Fact]
    public async Task A_matching_phone_counts_as_the_same_person()
    {
        // Deduplication is on either contact, not both. The same person listed
        // twice under one phone and two email addresses is one person, and
        // importing them twice means texting them twice.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("phone-dupe");
        var phone = "07700900" + Random.Shared.Next(100, 999);

        await ImportAsync(client, $"{Header}\nJane Smith,,{phone},\n");

        var again = await ImportAsync(client,
            $"{Header}\nJane S,different-{Guid.NewGuid():N}@example.test,{phone},\n");

        again.GetProperty("skipped").GetInt32().Should().Be(1);
        (await CountAsync(client)).Should().Be(1);
    }

    /* ── scoping ─────────────────────────────────────────────────────── */

    [Fact]
    public async Task An_import_lands_only_in_the_importing_pharmacy()
    {
        RequireDatabase();

        var (mine, _, _) = await Api.RegisterPharmacyAsync("scope-mine");
        var (theirs, _, _) = await Api.RegisterPharmacyAsync("scope-theirs");

        await ImportAsync(mine, $"{Header}\nJane Smith,scope-{Guid.NewGuid():N}@example.test,,\n");

        (await CountAsync(mine)).Should().Be(1);
        (await CountAsync(theirs)).Should().Be(
            0, "another pharmacy's import must not appear in this one's list");
    }

    [Fact]
    public async Task A_pharmacy_cannot_reach_the_admin_import()
    {
        // The pharmacy endpoint has no target parameter; the admin one does.
        // That is the whole reason they are separate controllers, so the policy
        // on the admin one has to hold.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("no-admin");
        var (_, _, victimId) = await Api.RegisterPharmacyAsync("victim");

        using var res = await client.PostAsync(
            $"/api/admin/patients/{victimId}/import",
            File($"{Header}\nIntruder,intruder@example.test,,\n"));

        ((int)res.StatusCode).Should().BeOneOf(
            401, 403, 404);
    }

    [Fact]
    public async Task A_pharmacy_cannot_export_another_pharmacys_contacts()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("no-export");
        var (_, _, victimId) = await Api.RegisterPharmacyAsync("export-victim");

        using var res = await client.GetAsync($"/api/admin/patients/{victimId}/export.csv");

        ((int)res.StatusCode).Should().BeOneOf(401, 403, 404);
    }

    /* ── exporting ───────────────────────────────────────────────────── */

    [Fact]
    public async Task An_export_returns_csv_not_json()
    {
        // The failure mode this product already had once: a non-JSON response
        // routed through a JSON path arrives as the word "null".
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("export-type");
        await ImportAsync(client, $"{Header}\nJane Smith,exp-{Guid.NewGuid():N}@example.test,,\n");

        using var res = await client.GetAsync("/api/patients/export.csv");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        res.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");

        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotBe("null");
        body.Should().StartWith("Full Name", "the header row comes first");
        body.Should().Contain("Jane Smith");
    }

    [Fact]
    public async Task An_export_contains_only_the_callers_contacts()
    {
        RequireDatabase();

        var mineEmail = $"mine-{Guid.NewGuid():N}@example.test";
        var theirsEmail = $"theirs-{Guid.NewGuid():N}@example.test";

        var (mine, _, _) = await Api.RegisterPharmacyAsync("export-mine");
        var (theirs, _, _) = await Api.RegisterPharmacyAsync("export-theirs");

        await ImportAsync(mine, $"{Header}\nMy Contact,{mineEmail},,\n");
        await ImportAsync(theirs, $"{Header}\nTheir Contact,{theirsEmail},,\n");

        using var res = await mine.GetAsync("/api/patients/export.csv");
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain(mineEmail);
        body.Should().NotContain(theirsEmail, "an export must not leak another pharmacy's list");
    }

    [Fact]
    public async Task An_exported_file_can_be_imported_back()
    {
        // The most common reason to export is to edit and re-upload. If the
        // round trip does not work, the feature is decorative.
        RequireDatabase();

        var (source, _, _) = await Api.RegisterPharmacyAsync("round-source");
        await ImportAsync(source,
            $"{Header}\n\"Smith, Jane\",round-{Guid.NewGuid():N}@example.test,07700900321,\"Notes, with comma\"\n");

        using var exported = await source.GetAsync("/api/patients/export.csv");
        var csv = await exported.Content.ReadAsStringAsync();

        var (target, _, _) = await Api.RegisterPharmacyAsync("round-target");
        var report = await ImportAsync(target, csv);

        report.GetProperty("created").GetInt32().Should().Be(
            1, "an export must be readable by the importer without editing");
        report.GetProperty("failed").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task The_template_is_a_csv_the_importer_accepts()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("template");

        using var res = await client.GetAsync("/api/patients/csv-template");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");

        var template = await res.Content.ReadAsStringAsync();
        var report = await ImportAsync(client, template);

        report.GetProperty("failed").GetInt32().Should().Be(
            0, "the template we hand out must import cleanly");
    }

    /* ── admin ───────────────────────────────────────────────────────── */

    [Fact]
    public async Task An_admin_can_import_into_a_chosen_pharmacy()
    {
        RequireDatabase();

        var (pharmacy, _, pharmacyId) = await Api.RegisterPharmacyAsync("admin-target");
        var admin = await Api.SignInSuperAdminAsync();

        using var res = await admin.PostAsync(
            $"/api/admin/patients/{pharmacyId}/import",
            File($"{Header}\nAdded By Support,support-{Guid.NewGuid():N}@example.test,,\n"));

        res.IsSuccessStatusCode.Should().BeTrue(
            "staff import must work: {0}", await res.Content.ReadAsStringAsync());

        (await CountAsync(pharmacy)).Should().Be(
            1, "the contact belongs to the pharmacy, not to the staff member who uploaded it");
    }
}
