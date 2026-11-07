using Acme.Application.Auth;
using Acme.Application.Messaging;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// The emails a pharmacy gets about its recurring posts: posted, needs you,
/// paused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once each.</b> An occurrence's outcome is claimed with a findAndModify
/// before this is called (see <c>ClaimNextUnreportedAsync</c>), so two API
/// instances cannot both report it. A pause is reported only by the write that
/// made it, and a paused series is never due, so it cannot repeat hourly.
/// </para>
/// <para>
/// Sent to the account's own address through the normal Mailjet path, as
/// transactional mail. They are not marketing, and patient-campaign
/// unsubscribes do not touch them; each series has its own setting (every
/// post / problems only / off), changed on the Recurring tab, and every email
/// says where.
/// </para>
/// <para>Never throws: a failed email must not stop the scheduler.</para>
/// </remarks>
public sealed class SeriesNotifier(
    IEmailSender email,
    IUserRepository users,
    ISocialConnectionRepository connections,
    IAuthLinkBuilder links,
    ILogger<SeriesNotifier> log) : ISeriesNotifier
{
    public async Task OccurrenceFinishedAsync(RecurringSeries series, ScheduledPost occurrence, CancellationToken ct)
    {
        try
        {
            if (series.Notify == SeriesNotify.Off) return;

            var problems = occurrence.Status is PublishStatus.Failed or PublishStatus.PartiallyPublished;
            if (!problems && series.Notify != SeriesNotify.All) return;

            var user = await users.FindByIdAsync(series.OwnerId, ct);
            if (user is null || !user.IsActive) return;

            var lines = new List<SeriesEmails.TargetLine>();
            foreach (var t in occurrence.Targets)
            {
                var c = await connections.FindByIdAsync(t.ConnectionId, ct);
                lines.Add(new SeriesEmails.TargetLine(
                    t.Platform.DisplayName(),
                    c?.DisplayName ?? "a disconnected account",
                    t.Status,
                    t.Error,
                    NeedsReconnect: c is null
                        || c.Status is ConnectionStatus.NeedsReauth or ConnectionStatus.Revoked
                        || (t.Error?.Contains("reconnect", StringComparison.OrdinalIgnoreCase) ?? false)));
            }

            var when = SeriesWording.Time(
                occurrence.OccurrenceAtUtc ?? occurrence.ScheduledForUtc ?? occurrence.CreatedAtUtc,
                series.Zone(), series.TimeZoneId).Label;

            var message = problems
                ? SeriesEmails.NeedsYou(user, series, when, lines, Links())
                : SeriesEmails.Posted(user, series, when, lines, Links());

            await email.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not email the outcome of occurrence {Post}", occurrence.Id);
        }
    }

    public async Task SeriesPausedAsync(RecurringSeries series, string reason, CancellationToken ct)
    {
        try
        {
            if (series.Notify == SeriesNotify.Off) return;
            var user = await users.FindByIdAsync(series.OwnerId, ct);
            // A deactivated account is not emailed about its own deactivation's side effects.
            if (user is null || !user.IsActive) return;
            await email.SendAsync(SeriesEmails.Paused(user, series, reason, Links()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not email that series {Series} was paused", series.Id);
        }
    }

    private SeriesEmails.AppLinks Links() => new(
        links.BuildAppLink("/social?tab=recurring"),
        links.BuildAppLink("/social?tab=accounts"),
        links.BuildAppLink("/pricing"));
}

/// <summary>The three series emails. Every value goes through <see cref="TransactionalEmail"/>, which escapes it.</summary>
public static class SeriesEmails
{
    public sealed record TargetLine(
        string PlatformName, string AccountName, PublishTargetStatus Status, string? Error, bool NeedsReconnect);

    public sealed record AppLinks(string Recurring, string Accounts, string Pricing);

    private const string WhyYouGetThis =
        "You get these because of this series' email setting. Change it, or turn them off, "
        + "on the Recurring tab under Social & Ads.";

    public static EmailMessage Posted(
        Acme.Domain.Users.User user, RecurringSeries series, string when, IReadOnlyList<TargetLine> targets, AppLinks links)
    {
        var sent = targets.Where(t => t.Status == PublishTargetStatus.Published)
            .Select(t => $"{t.PlatformName} ({t.AccountName})").ToList();

        return TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: $"Posted: {series.Name}",
            heading: "Your recurring post went out",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: new[]
            {
                $"“{series.Name}” was posted for {when} to {Join(sent)}.",
                series.NextOccurrenceUtc is { } next
                    ? $"Next post: {SeriesWording.Time(next, series.Zone(), series.TimeZoneId).Label}."
                    : "That was the last post in this series.",
            },
            action: new TransactionalEmail.Action("See your recurring posts", links.Recurring),
            footnotes: new[] { WhyYouGetThis });
    }

    public static EmailMessage NeedsYou(
        Acme.Domain.Users.User user, RecurringSeries series, string when, IReadOnlyList<TargetLine> targets, AppLinks links)
    {
        var sent = targets.Where(t => t.Status == PublishTargetStatus.Published).ToList();
        var problems = targets.Where(t => t.Status is PublishTargetStatus.Failed or PublishTargetStatus.Uncertain).ToList();
        var reconnect = problems.Any(t => t.NeedsReconnect);

        var paragraphs = new List<string>
        {
            sent.Count == 0
                ? $"“{series.Name}” did not go out for {when}."
                : $"“{series.Name}” went out for {when} to "
                  + $"{Join(sent.Select(t => $"{t.PlatformName} ({t.AccountName})"))}, but not everywhere.",
        };

        foreach (var p in problems)
        {
            paragraphs.Add(p.Status == PublishTargetStatus.Uncertain
                ? $"{p.PlatformName} ({p.AccountName}): it may have gone out. Check the account before you retry it."
                : p.NeedsReconnect
                    ? $"{p.PlatformName} ({p.AccountName}): this account needs to be reconnected."
                    : $"{p.PlatformName} ({p.AccountName}): {p.Error ?? "it was refused without a reason."}");
        }

        paragraphs.Add(series.Status == SeriesStatus.Active
            ? "The series carries on as planned. Only this post was affected."
            : "This series is not running at the moment.");

        return TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: reconnect ? $"Reconnect needed: {series.Name}" : $"Needs you: {series.Name}",
            heading: reconnect ? "An account needs reconnecting" : "A recurring post needs you",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: paragraphs,
            action: reconnect
                ? new TransactionalEmail.Action("Reconnect the account", links.Accounts)
                : new TransactionalEmail.Action("See what happened", links.Recurring),
            footnotes: new[] { WhyYouGetThis });
    }

    public static EmailMessage Paused(Acme.Domain.Users.User user, RecurringSeries series, string reason, AppLinks links)
    {
        var plan = reason.Contains("plan", StringComparison.OrdinalIgnoreCase);
        return TransactionalEmail.To(
            recipient: user.Email.Value,
            subject: $"Paused: {series.Name}",
            heading: "A recurring post has been paused",
            greetingName: TransactionalEmail.FirstName(user.DisplayName),
            paragraphs: new[]
            {
                $"We paused “{series.Name}” and did not post it. {reason}",
                "Nothing already posted has been changed. Once that is sorted, resume the series "
                + "and it carries on from the next date.",
            },
            action: plan
                ? new TransactionalEmail.Action("See plans", links.Pricing)
                : new TransactionalEmail.Action("Open your recurring posts", links.Recurring),
            footnotes: new[] { WhyYouGetThis });
    }

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "no accounts",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
