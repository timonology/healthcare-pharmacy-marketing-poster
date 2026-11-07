using Acme.Domain.Campaigns;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Campaigns;

/// <summary>
/// Who a campaign still needs to send to, across retries and interruptions.
/// </summary>
/// <remarks>
/// <para>
/// These are domain tests with no database and no provider, because the rules
/// they cover are domain rules: dispatch sends to
/// <see cref="Campaign.PendingRecipients"/>, and everything else — retry,
/// resume after a crash, two workers racing — is a consequence of that one
/// decision rather than a separate mechanism to be tested separately.
/// </para>
/// <para>
/// <b>The first test is the one that matters.</b> Retry used to dispatch to
/// the whole original recipient list while the per-recipient report sat beside
/// it, written and never read, so a campaign that reached 700 of 1000 and
/// failed sent to all 1000 again on Retry. The integration suite could not see
/// it: every campaign test there sends to exactly one recipient, where a send
/// either wholly works or wholly fails and retry is correct by accident.
/// </para>
/// </remarks>
public class CampaignDispatchStateTests
{
    private static Campaign WithRecipients(int count)
    {
        var campaign = Campaign.Create(
            ownerId: "owner-1",
            posterId: "poster-1",
            name: "Flu clinic",
            channel: CampaignChannel.Email,
            recipients: Enumerable.Range(0, count).Select(i => $"p{i}@example.test"));

        campaign.Queue();
        return campaign;
    }

    private static void MarkDelivered(Campaign campaign, int howMany) =>
        campaign.RecordResults(campaign.Recipients.Take(howMany).Select(r => new RecipientResult
        {
            Recipient = r,
            Status = RecipientStatus.Sent,
            AtUtc = DateTime.UtcNow,
        }));

    private static void MarkFailed(Campaign campaign, IEnumerable<string> who, string reason) =>
        campaign.RecordResults(who.Select(r => new RecipientResult
        {
            Recipient = r,
            Status = RecipientStatus.Failed,
            Reason = reason,
            AtUtc = DateTime.UtcNow,
        }));

    [Fact]
    public void A_retry_after_a_partial_send_does_not_send_to_anyone_twice()
    {
        var campaign = WithRecipients(1000);

        // 700 delivered, then the send falls over.
        MarkDelivered(campaign, 700);
        MarkFailed(campaign, campaign.Recipients.Skip(700), "Could not reach the provider");
        campaign.Complete("300 recipient(s) failed.");

        campaign.Status.Should().Be(CampaignStatus.Sent, "700 people did receive it");
        campaign.SentCount.Should().Be(700);

        // The pharmacist presses Retry.
        campaign.Queue();

        campaign.PendingRecipients.Should().HaveCount(300,
            "only the 300 who did not get it are outstanding. Sending to 1000 again "
            + "would deliver the same clinical marketing twice to 700 patients, from a "
            + "pharmacy with no way to know it had happened");

        campaign.PendingRecipients.Should().NotContain(campaign.Recipients.Take(700),
            "nobody who has already received it may be sent to again");
    }

    [Fact]
    public void Recipients_delivered_before_an_interruption_survive_it()
    {
        // The crash case. A worker is killed after two groups; the campaign is
        // still marked Sending with a lease nobody will renew.
        var campaign = WithRecipients(100);
        campaign.Claim("worker-a", DateTime.UtcNow.AddMinutes(5));
        MarkDelivered(campaign, 40);

        // Its lease lapses and another worker claims it.
        campaign.Queue();
        campaign.Claim("worker-b", DateTime.UtcNow.AddMinutes(5));

        campaign.PendingRecipients.Should().HaveCount(60,
            "the 40 recorded before the interruption are not sent again");
        campaign.DeliveredCount.Should().Be(40);
    }

    [Fact]
    public void Results_are_recorded_as_the_send_progresses_not_at_the_end()
    {
        // Why the dispatcher writes after every group. Whatever is durable at
        // the moment of a crash is what will not be re-sent.
        var campaign = WithRecipients(60);
        campaign.Claim("worker-a", DateTime.UtcNow.AddMinutes(5));

        campaign.DeliveredCount.Should().Be(0);

        MarkDelivered(campaign, 20);
        campaign.DeliveredCount.Should().Be(20, "the first group is already durable");
        campaign.SentCount.Should().Be(20, "the count the UI shows follows the report");

        MarkDelivered(campaign, 40);
        campaign.DeliveredCount.Should().Be(40);
    }

    [Fact]
    public void A_campaign_where_every_recipient_failed_is_Failed()
    {
        var campaign = WithRecipients(10);
        MarkFailed(campaign, campaign.Recipients, "Mailbox unavailable");
        campaign.Complete();

        campaign.Status.Should().Be(CampaignStatus.Failed);
        campaign.SentCount.Should().Be(0);
    }

    [Fact]
    public void A_campaign_that_reached_almost_everyone_is_not_called_a_failure()
    {
        // 998 of 1000 is not a failed campaign, and calling it one would hide
        // the 998 from the pharmacist — who would then quite reasonably retry
        // it, which under the old code meant sending to all 1000 again.
        var campaign = WithRecipients(1000);
        MarkDelivered(campaign, 998);
        MarkFailed(campaign, campaign.Recipients.Skip(998), "Rejected");
        campaign.Complete("2 recipient(s) failed.");

        campaign.Status.Should().Be(CampaignStatus.Sent);
        campaign.SentCount.Should().Be(998);
        campaign.PendingRecipients.Should().BeEmpty("nothing is outstanding until a retry");
    }

    [Fact]
    public void Queueing_a_fresh_campaign_marks_every_recipient_pending()
    {
        // The list of who still needs sending exists before the first message
        // goes out, so a crash one second in looks the same as a crash halfway.
        var campaign = WithRecipients(250);

        campaign.Status.Should().Be(CampaignStatus.Queued);
        campaign.DeliveryReport.Should().HaveCount(250);
        campaign.DeliveryReport.Should().OnlyContain(r => r.Status == RecipientStatus.Pending);
        campaign.PendingRecipients.Should().HaveCount(250);
    }

    [Fact]
    public void A_campaign_everyone_has_received_cannot_be_queued_again()
    {
        var campaign = WithRecipients(5);
        MarkDelivered(campaign, 5);
        campaign.Complete();

        var again = () => campaign.Queue();
        again.Should().Throw<Acme.Domain.Common.DomainException>(
            "there is nobody left to send to");
    }

    [Fact]
    public void A_partly_sent_campaign_can_be_queued_again()
    {
        // The guard is on what is outstanding, not on the status, and this is
        // why. A campaign that reached 700 of 1000 is Sent — 700 people did
        // receive it — so refusing to queue a Sent campaign would have made
        // retrying those 300 impossible, which is the case the delivery report
        // exists for. Writing this test is what found it.
        var campaign = WithRecipients(1000);
        MarkDelivered(campaign, 700);
        MarkFailed(campaign, campaign.Recipients.Skip(700), "Rejected");
        campaign.Complete();

        campaign.Status.Should().Be(CampaignStatus.Sent);

        var again = () => campaign.Queue();
        again.Should().NotThrow();
        campaign.PendingRecipients.Should().HaveCount(300);
    }

    [Fact]
    public void A_stopped_campaign_cannot_be_queued_again()
    {
        var stopped = WithRecipients(5);
        stopped.Stop();

        var again = () => stopped.Queue();
        again.Should().Throw<Acme.Domain.Common.DomainException>();
    }

    [Fact]
    public void Stopping_releases_the_lease()
    {
        // Otherwise a worker keeps a campaign nobody wants sent, until the
        // lease lapses on its own.
        var campaign = WithRecipients(10);
        campaign.Claim("worker-a", DateTime.UtcNow.AddMinutes(5));

        campaign.Stop();

        campaign.LeaseOwner.Should().BeNull();
        campaign.LeaseExpiresAtUtc.Should().BeNull();
    }
}
