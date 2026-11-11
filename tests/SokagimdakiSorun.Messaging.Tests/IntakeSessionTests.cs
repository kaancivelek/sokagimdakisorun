using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Messaging.Domain;

namespace SokagimdakiSorun.Messaging.Tests;

/// <summary>Intake durum makinesi (DOMAIN_MODEL §5): yalnızca diyagramdaki geçişler, onaysız şikâyet yok.</summary>
public sealed class IntakeSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly IntakeRules Rules = new(TimeSpan.FromHours(2), MaxPhotos: 3, MaxInvalidInputs: 3);
    private static readonly ComplaintCategorySummary Road = new(Guid.CreateVersion7(), "yol-kaldirim", "Yol ve Kaldırım", null, 1);
    private static readonly ComplaintCategorySummary Light = new(Guid.CreateVersion7(), "aydinlatma", "Aydınlatma", null, 2);
    private static readonly IReadOnlyList<ComplaintCategorySummary> Categories = [Road, Light];

    [Fact]
    public void Happy_path_requests_submit_only_after_explicit_confirmation()
    {
        var (session, start) = IntakeSession.Start(Guid.CreateVersion7(), Categories, null, T0, Rules);
        Assert.Equal(IntakeState.AwaitingCategory, session.State);
        Assert.Contains("1. Yol ve Kaldırım", Assert.Single(start.Replies), StringComparison.Ordinal);

        Step(session, Text("2"), IntakeState.AwaitingDescription);
        Assert.Equal(Light.Id, session.CategoryId);
        Step(session, Text("Sokak lambası yanmıyor."), IntakeState.AwaitingLocation);
        Step(session, Location(40.41, 26.67), IntakeState.AwaitingMedia);
        var summary = Step(session, Photo(), IntakeState.AwaitingConfirmation);
        Assert.Contains("Aydınlatma", summary.Replies[0], StringComparison.Ordinal);
        Assert.Contains("Fotoğraf: 1 adet", summary.Replies[0], StringComparison.Ordinal);

        // Onaydan başka hiçbir girdi şikâyet oluşturmaz.
        Assert.False(Step(session, Text("tamam belki"), IntakeState.AwaitingConfirmation).SubmitRequested);
        Assert.True(Step(session, Text("EVET"), IntakeState.AwaitingConfirmation).SubmitRequested);

        var complaintId = Guid.CreateVersion7();
        session.MarkSubmitted(complaintId, T0);
        session.MarkSubmitted(complaintId, T0); // idempotent
        Assert.Equal((IntakeState.Submitted, complaintId), (session.State, session.SubmittedComplaintId));
        Assert.False(session.IsActive);
    }

    [Theory]
    [InlineData("iptal")]
    [InlineData("İPTAL")]
    [InlineData("Vazgeç")]
    public void Cancel_works_from_any_active_step(string command)
    {
        foreach (var steps in new[] { 0, 1, 2, 3, 4 })
        {
            var session = Advanced(steps);
            var outcome = session.Advance(Text(command), Categories, T0, Rules);
            Assert.Equal(IntakeState.Cancelled, session.State);
            Assert.Equal(BotMessages.Cancelled, Assert.Single(outcome.Replies));
            Assert.NotNull(session.CompletedAt);
        }
    }

    [Fact]
    public void Corrections_return_directly_to_summary()
    {
        var session = Advanced(4);
        Assert.Equal(IntakeState.AwaitingConfirmation, session.State);

        Step(session, Text("3"), IntakeState.AwaitingDescription);
        Step(session, Text("Düzeltilmiş açıklama."), IntakeState.AwaitingConfirmation);
        Assert.Equal("Düzeltilmiş açıklama.", session.DescriptionDraft);

        Step(session, Text("4"), IntakeState.AwaitingLocation);
        Step(session, Text("Yazıcızade Mah. (temsili)"), IntakeState.AwaitingConfirmation);
        Assert.Equal(("Yazıcızade Mah. (temsili)", (double?)null), (session.AddressText, session.Latitude));

        Step(session, Text("2"), IntakeState.AwaitingCategory);
        Step(session, Text("yol ve kaldırım"), IntakeState.AwaitingConfirmation); // ada göre de seçilebilir
        Assert.Equal(Road.Id, session.CategoryId);
    }

    [Fact]
    public void Unsupported_type_is_explained_and_does_not_advance()
    {
        var session = Advanced(1); // AwaitingDescription
        var outcome = Step(session, new IntakeInput(MessageType.Unsupported, null, null, null, null, false), IntakeState.AwaitingDescription);
        Assert.Equal(BotMessages.Unsupported, outcome.Replies[0]);

        var media = Advanced(3); // AwaitingMedia
        Assert.Equal(BotMessages.Unsupported,
            Step(media, new IntakeInput(MessageType.Unsupported, null, null, null, null, false), IntakeState.AwaitingMedia).Replies[0]);
    }

    [Fact]
    public void Repeated_invalid_input_leads_to_needs_human_help()
    {
        var session = Advanced(0);
        Step(session, Text("99"), IntakeState.AwaitingCategory);
        Step(session, Text("bilmiyorum"), IntakeState.AwaitingCategory);
        var outcome = session.Advance(Text("?"), Categories, T0, Rules);
        Assert.Equal(IntakeState.NeedsHumanHelp, session.State);
        Assert.Equal(BotMessages.NeedsHumanHelp, Assert.Single(outcome.Replies));
    }

    [Fact]
    public void Rejected_photo_keeps_waiting_and_skip_moves_to_summary()
    {
        var session = Advanced(3);
        Assert.Equal(BotMessages.MediaRejected,
            Step(session, new IntakeInput(MessageType.Image, null, null, null, null, true), IntakeState.AwaitingMedia).Replies[0]);
        Step(session, Text("atla"), IntakeState.AwaitingConfirmation);
        Assert.Empty(session.MediaFileIds);

        for (var i = 0; i < 5; i++)
        {
            Step(session, Photo(), IntakeState.AwaitingConfirmation);
        }

        Assert.Equal(Rules.MaxPhotos, session.MediaFileIds.Count);
    }

    [Fact]
    public void Expiry_applies_only_after_timeout_and_activity_extends_it()
    {
        var session = Advanced(2);
        Assert.False(session.ExpireIfDue(T0 + TimeSpan.FromHours(1)));
        session.Advance(Location(40.41, 26.67), Categories, T0 + TimeSpan.FromHours(1), Rules);
        Assert.False(session.ExpireIfDue(T0 + TimeSpan.FromHours(2.5)));
        Assert.True(session.ExpireIfDue(T0 + TimeSpan.FromHours(3)));
        Assert.Equal(IntakeState.Expired, session.State);
        Assert.False(session.ExpireIfDue(T0 + TimeSpan.FromHours(4)));
        Assert.Throws<InvalidOperationException>(() => session.Advance(Text("1"), Categories, T0, Rules));
    }

    [Fact]
    public void Rejected_category_on_submit_returns_to_category_then_summary()
    {
        var session = Advanced(4);
        session.ReturnToCategoryAfterRejectedSubmit([Road], T0);
        Assert.Equal((IntakeState.AwaitingCategory, (Guid?)null), (session.State, session.CategoryId));
        Step(session, Text("1"), IntakeState.AwaitingConfirmation);
        Assert.Equal(Road.Id, session.CategoryId);
    }

    [Fact]
    public void No_active_categories_ends_in_needs_human_help()
    {
        var (session, outcome) = IntakeSession.Start(Guid.CreateVersion7(), [], null, T0, Rules);
        Assert.Equal(IntakeState.NeedsHumanHelp, session.State);
        Assert.Equal(BotMessages.NoCategories, Assert.Single(outcome.Replies));
    }

    private static IntakeSession Advanced(int steps)
    {
        var (session, _) = IntakeSession.Start(Guid.CreateVersion7(), Categories, null, T0, Rules);
        IntakeInput[] inputs = [Text("1"), Text("Kaldırım kırık."), Location(40.41, 26.67), Text("atla")];
        foreach (var input in inputs.Take(steps))
        {
            session.Advance(input, Categories, T0, Rules);
        }

        return session;
    }

    private static IntakeOutcome Step(IntakeSession session, IntakeInput input, IntakeState expected)
    {
        var outcome = session.Advance(input, Categories, T0, Rules);
        Assert.Equal(expected, session.State);
        return outcome;
    }

    private static IntakeInput Text(string text) => new(MessageType.Text, text, null, null, null, false);

    private static IntakeInput Location(double lat, double lon) => new(MessageType.Location, null, lat, lon, null, false);

    private static IntakeInput Photo() => new(MessageType.Image, null, null, null, Guid.CreateVersion7(), false);
}
