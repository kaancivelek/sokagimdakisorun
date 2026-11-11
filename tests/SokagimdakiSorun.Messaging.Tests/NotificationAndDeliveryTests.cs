using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Messaging.Application;
using SokagimdakiSorun.Messaging.Domain;
using SokagimdakiSorun.Messaging.Endpoints;

namespace SokagimdakiSorun.Messaging.Tests;

public sealed class NotificationAndDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Policy_table_has_an_explicit_decision_for_every_status_except_submitted()
    {
        var expected = Enum.GetValues<ComplaintStatus>().Where(s => s != ComplaintStatus.Submitted).Order();
        Assert.Equal(expected, NotificationPolicy.CoveredStatuses.Order());
    }

    [Theory]
    [InlineData(ComplaintStatus.UnderReview, ComplaintStatus.AwaitingInformation, true)]
    [InlineData(ComplaintStatus.Assigned, ComplaintStatus.InProgress, true)]
    [InlineData(ComplaintStatus.InProgress, ComplaintStatus.Resolved, true)]
    [InlineData(ComplaintStatus.Submitted, ComplaintStatus.Rejected, true)]
    [InlineData(ComplaintStatus.UnderReview, ComplaintStatus.Duplicate, true)]
    [InlineData(ComplaintStatus.AwaitingInformation, ComplaintStatus.Withdrawn, true)]
    [InlineData(ComplaintStatus.Accepted, ComplaintStatus.Closed, true)]
    [InlineData(ComplaintStatus.Resolved, ComplaintStatus.Closed, false)]
    [InlineData(ComplaintStatus.Submitted, ComplaintStatus.UnderReview, false)]
    [InlineData(ComplaintStatus.UnderReview, ComplaintStatus.Accepted, false)]
    [InlineData(ComplaintStatus.Accepted, ComplaintStatus.Assigned, false)]
    [InlineData(ComplaintStatus.Submitted, ComplaintStatus.Spam, false)]
    public void Policy_matches_api_contract_table(ComplaintStatus from, ComplaintStatus to, bool notify) =>
        Assert.Equal(notify, NotificationPolicy.ForStatusChange(from, to, "SS-AAAA-BBBB", null) is not null);

    [Fact]
    public void Texts_include_reference_and_reporter_note()
    {
        Assert.Contains("SS-AAAA-BBBB", NotificationPolicy.ForSubmitted("SS-AAAA-BBBB"), StringComparison.Ordinal);
        var question = NotificationPolicy.ForStatusChange(ComplaintStatus.UnderReview, ComplaintStatus.AwaitingInformation, "SS-AAAA-BBBB", "Konum?");
        Assert.Contains("Konum?", question, StringComparison.Ordinal);
        var resolved = NotificationPolicy.ForStatusChange(ComplaintStatus.InProgress, ComplaintStatus.Resolved, "SS-AAAA-BBBB", "Yama yapıldı.");
        Assert.EndsWith("Not: Yama yapıldı.", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_status_only_moves_forward()
    {
        var message = Message.Outbound(Guid.CreateVersion7(), "x", "event:1", null, null, Now);
        message.BeginSending(Now.AddMinutes(1), Now);
        message.MarkSent("wamid.1", Now);

        Assert.True(message.ApplyProviderStatus(DeliveryStatus.Read, Now, null, Now));
        Assert.False(message.ApplyProviderStatus(DeliveryStatus.Delivered, Now, null, Now)); // sırası bozuk teslim
        Assert.False(message.ApplyProviderStatus(DeliveryStatus.Failed, Now, "provider_error:1", Now));
        Assert.Equal(DeliveryStatus.Read, message.DeliveryStatus);
    }

    [Fact]
    public void Retry_delay_grows_exponentially_with_bounded_jitter_and_cap()
    {
        var first = RetryDelay.Compute(1, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 0);
        var third = RetryDelay.Compute(3, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 1);
        var capped = RetryDelay.Compute(30, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), 1);
        Assert.Equal(TimeSpan.FromSeconds(5), first);
        Assert.Equal(TimeSpan.FromSeconds(40), third);
        Assert.Equal(TimeSpan.FromMinutes(5), capped);
    }

    [Fact]
    public void Service_window_is_24_hours_from_last_inbound()
    {
        var conversation = Conversation.Create(Guid.CreateVersion7(), "1", "905550000001", Now);
        Assert.False(conversation.IsServiceWindowOpen(Now, TimeSpan.FromHours(24)));
        conversation.RecordInbound(Now, Now);
        Assert.True(conversation.IsServiceWindowOpen(Now.AddHours(23.9), TimeSpan.FromHours(24)));
        Assert.False(conversation.IsServiceWindowOpen(Now.AddHours(24), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Awaiting_reply_context_ignores_stale_events()
    {
        var conversation = Conversation.Create(Guid.CreateVersion7(), "1", "905550000001", Now);
        var complaint = Guid.CreateVersion7();
        conversation.AwaitReplyFor(complaint, Now, Now);
        conversation.StopAwaitingReplyFor(complaint, Now.AddMinutes(5), Now);
        conversation.AwaitReplyFor(complaint, Now.AddMinutes(-1), Now); // eski olay yeniden teslim edildi
        Assert.Null(conversation.AwaitingReplyComplaintId);
    }

    [Theory]
    [InlineData("905551234567", "90********67")]
    [InlineData("1234", "****")]
    public void Phone_is_masked(string value, string expected) => Assert.Equal(expected, PhoneMasking.Mask(value));
}
