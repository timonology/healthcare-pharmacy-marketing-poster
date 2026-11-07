using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Patients;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Patients;

/// <summary>What happened to one row, in the language the operator needs.</summary>
public sealed record PatientImportRow(int LineNumber, string Outcome, string Detail, string? Contact);

/// <summary>The report shown after an import.</summary>
public sealed record PatientImportReport(
    int TotalRows,
    int Created,
    int Skipped,
    int Failed,
    IReadOnlyList<PatientImportRow> Rows);

/// <summary>
/// Bringing a pharmacy's existing contact list into Marketing25.
/// </summary>
/// <remarks>
/// <para>
/// The report is the feature. Anyone importing a real list has one already —
/// exported from a till system or kept in a spreadsheet — and it will be
/// messy. "412 of 500 imported" without saying which 88 and why leaves them
/// unable to fix anything, so every row that does not become a contact is
/// returned with its line number and a reason a human can act on.
/// </para>
/// <para>
/// <b>Scoping is the caller's responsibility to get right and this service's to
/// enforce.</b> Every patient created here is stamped with the owner passed in,
/// and duplicates are checked against that owner's contacts only — so an
/// import can never reach across pharmacies, whatever the caller intended.
/// </para>
/// </remarks>
public sealed class PatientImportService(
    IPatientRepository patients,
    IUserRepository users,
    ILogger<PatientImportService> log)
{
    /// <summary>
    /// A cap on one file, so a mistaken upload cannot fill a pharmacy's list.
    /// </summary>
    /// <remarks>
    /// Generous enough for a real pharmacy list and small enough that the whole
    /// import stays a single request. Beyond this the file is rejected whole
    /// rather than half-applied, because a partial import of an unknown
    /// quantity is worse than none.
    /// </remarks>
    public const int MaxRowsPerFile = 5_000;

    private const string Created = "created";
    private const string Skipped = "skipped";
    private const string Failed = "failed";

    public async Task<Result<PatientImportReport>> ImportAsync(
        string ownerId, string csv, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return Result<PatientImportReport>.Invalid("No pharmacy to import into.");

        if (string.IsNullOrWhiteSpace(csv))
            return Result<PatientImportReport>.Invalid("The file is empty.");

        var parsed = PatientCsv.Parse(csv);

        // A header problem is reported as a failure of the file rather than of
        // its rows — there is nothing per-row to say when no column matched.
        if (parsed.Rows.Count == 0 && parsed.DataRows == 0)
            return Result<PatientImportReport>.Invalid(
                parsed.Issues.FirstOrDefault()?.Reason ?? "Nothing in this file could be read.");

        if (parsed.DataRows > MaxRowsPerFile)
            return Result<PatientImportReport>.Invalid(
                $"That file has {parsed.DataRows:N0} rows. The most that can be imported at "
                + $"once is {MaxRowsPerFile:N0} — please split it.");

        // Existing contacts, so a re-import of the same list adds nothing.
        // Loaded once rather than queried per row: a 500-row file would
        // otherwise be 500 round trips, and the set is small enough to hold.
        var existing = await patients.ListAsync(
            new PatientQuery(ownerId, Skip: 0, Take: int.MaxValue), ct);

        var takenEmails = existing
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            .Select(p => p.Email!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var takenPhones = existing
            .Where(p => !string.IsNullOrWhiteSpace(p.Phone))
            .Select(p => Digits(p.Phone!))
            .ToHashSet(StringComparer.Ordinal);

        // The plan ceiling, shared with the form-based path. Without this an
        // import is a way round a limit the rest of the product enforces —
        // a Free pharmacy blocked from adding a 51st contact by hand could add
        // five thousand from a file.
        var allowance = await PatientLimits.RemainingAsync(users, patients, ownerId, ct);
        if (allowance.Error is not null)
            return Result<PatientImportReport>.Forbidden(allowance.Error);

        var room = allowance.Remaining;

        var rows = new List<PatientImportRow>();
        var toCreate = new List<Patient>();

        // Parse issues are failures, and keep their line numbers so the report
        // reads in file order rather than in two sections.
        foreach (var issue in parsed.Issues)
            rows.Add(new PatientImportRow(issue.LineNumber, Failed, issue.Reason, issue.Value));

        foreach (var row in parsed.Rows)
        {
            var email = row.Email?.Trim().ToLowerInvariant();
            var phone = row.Phone is null ? null : Digits(row.Phone);

            // Matching on either contact, not both. Somebody in the list twice
            // under one email and two phone numbers is one person, and
            // importing them twice would email them twice.
            if (email is not null && takenEmails.Contains(email))
            {
                rows.Add(new PatientImportRow(
                    row.LineNumber, Skipped, "Already in this pharmacy's contacts.", email));
                continue;
            }

            if (phone is not null && phone.Length > 0 && takenPhones.Contains(phone))
            {
                rows.Add(new PatientImportRow(
                    row.LineNumber, Skipped, "Already in this pharmacy's contacts.", row.Phone));
                continue;
            }

            // Out of room. Reported per row rather than trimming the file
            // silently, so the operator knows exactly which contacts did not
            // make it and why.
            if (room <= 0)
            {
                rows.Add(new PatientImportRow(
                    row.LineNumber, Skipped,
                    $"Not added — the {allowance.PlanName} plan allows {allowance.Limit:N0} contacts.",
                    email ?? row.Phone));
                continue;
            }

            Patient patient;
            try
            {
                patient = Patient.Create(ownerId, row.FullName, row.Email, row.Phone, row.Notes);
                if (row.MarketingConsent == true)
                    patient.RecordMarketingConsent(true, "Imported from a contact file");
            }
            catch (Exception ex)
            {
                // Should not happen — Parse already ran this — but a row that
                // fails here must be reported rather than aborting the file.
                rows.Add(new PatientImportRow(row.LineNumber, Failed, ex.Message, email ?? row.Phone));
                continue;
            }

            toCreate.Add(patient);
            room--;
            if (email is not null) takenEmails.Add(email);
            if (phone is not null && phone.Length > 0) takenPhones.Add(phone);

            rows.Add(new PatientImportRow(row.LineNumber, Created, "Added.", email ?? row.Phone));
        }

        if (toCreate.Count > 0)
            await patients.AddManyAsync(toCreate, ct);

        var report = new PatientImportReport(
            TotalRows: parsed.DataRows,
            Created: rows.Count(r => r.Outcome == Created),
            Skipped: rows.Count(r => r.Outcome == Skipped),
            Failed: rows.Count(r => r.Outcome == Failed),
            Rows: rows.OrderBy(r => r.LineNumber).ToList());

        log.LogInformation(
            "Patient import for {Owner}: {Created} created, {Skipped} skipped, {Failed} failed, of {Total} rows",
            ownerId, report.Created, report.Skipped, report.Failed, report.TotalRows);

        return Result<PatientImportReport>.Success(report);
    }

    /// <summary>Every contact for one pharmacy, as CSV.</summary>
    public async Task<Result<string>> ExportAsync(string ownerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return Result<string>.Invalid("No pharmacy to export.");

        var all = await patients.ListAsync(
            new PatientQuery(ownerId, Skip: 0, Take: int.MaxValue), ct);

        log.LogInformation("Exported {Count} contact(s) for {Owner}", all.Count, ownerId);
        return Result<string>.Success(PatientCsv.Write(all));
    }

    /// <summary>Phone reduced to comparable digits, matching Patient's own normalisation.</summary>
    private static string Digits(string phone) =>
        new(phone.Where(c => c == '+' || char.IsDigit(c)).ToArray());
}
