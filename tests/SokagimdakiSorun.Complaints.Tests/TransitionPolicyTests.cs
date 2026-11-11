using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;
using SokagimdakiSorun.SharedKernel;
using S = SokagimdakiSorun.Complaints.Contracts.ComplaintStatus;
using C = SokagimdakiSorun.Complaints.Domain.ComplaintCommand;

namespace SokagimdakiSorun.Complaints.Tests;

/// <summary>
/// DOMAIN_MODEL §4.3 geçiş tablosu. Beklenen tablo burada, koddan bağımsız olarak dokümandan yeniden yazılmıştır;
/// politika tablosu dokümandan saparsa testler kırılır.
/// </summary>
public sealed class TransitionPolicyTests
{
    /// <summary>(#, komut, nereden, nereye, kim, gerekçe zorunlu, soru zorunlu, giriş durumu).</summary>
    private static readonly (int Number, C Command, S? From, S To, ActorRole Actor, bool Reason, bool Note, S? Entry)[] Table =
    [
        (1, C.SubmitFromIntake, null, S.Submitted, ActorRole.System, false, false, null),
        (2, C.StartReview, S.Submitted, S.UnderReview, ActorRole.Moderator, false, false, null),
        (3, C.Reject, S.Submitted, S.Rejected, ActorRole.Moderator, true, false, null),
        (4, C.MarkDuplicate, S.Submitted, S.Duplicate, ActorRole.Moderator, true, false, null),
        (5, C.MarkSpam, S.Submitted, S.Spam, ActorRole.Moderator, true, false, null),
        (6, C.RequestInformation, S.UnderReview, S.AwaitingInformation, ActorRole.Moderator, true, true, null),
        (7, C.Accept, S.UnderReview, S.Accepted, ActorRole.Moderator, false, false, null),
        (8, C.Reject, S.UnderReview, S.Rejected, ActorRole.Moderator, true, false, null),
        (9, C.MarkDuplicate, S.UnderReview, S.Duplicate, ActorRole.Moderator, true, false, null),
        (10, C.MarkSpam, S.UnderReview, S.Spam, ActorRole.Moderator, true, false, null),
        (11, C.ProvideInformation, S.AwaitingInformation, S.UnderReview, ActorRole.Reporter, false, false, S.UnderReview),
        (11, C.Resume, S.AwaitingInformation, S.UnderReview, ActorRole.Moderator, false, false, S.UnderReview),
        (12, C.Withdraw, S.AwaitingInformation, S.Withdrawn, ActorRole.Reporter, false, false, null),
        (13, C.Assign, S.Accepted, S.Assigned, ActorRole.Moderator, false, false, null),
        (14, C.Close, S.Accepted, S.Closed, ActorRole.Moderator, true, false, null),
        (15, C.StartWork, S.Assigned, S.InProgress, ActorRole.Moderator, false, false, null),
        (16, C.Unassign, S.Assigned, S.Accepted, ActorRole.Moderator, true, false, null),
        (17, C.Resolve, S.InProgress, S.Resolved, ActorRole.Moderator, true, false, null),
        (18, C.RequestInformation, S.InProgress, S.AwaitingInformation, ActorRole.Moderator, true, true, null),
        (19, C.Reassign, S.InProgress, S.Assigned, ActorRole.Moderator, true, false, null),
        (20, C.Close, S.Resolved, S.Closed, ActorRole.Moderator, false, false, null),
        (21, C.Reopen, S.Resolved, S.UnderReview, ActorRole.Moderator, true, false, null),
        (22, C.Reopen, S.Closed, S.UnderReview, ActorRole.Admin, true, false, null),
        (23, C.ProvideInformation, S.AwaitingInformation, S.InProgress, ActorRole.Reporter, false, false, S.InProgress),
        (23, C.Resume, S.AwaitingInformation, S.InProgress, ActorRole.Moderator, false, false, S.InProgress),
    ];

    /// <summary>Tablo satırlarının sırası (iç tipler public test imzasında kullanılamadığı için satır indeksiyle).</summary>
    public static TheoryData<int> Rows => [.. Enumerable.Range(0, Table.Length)];

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_documented_transition_is_allowed_for_its_actor(int row)
    {
        var (number, command, from, to, actor, reasonRequired, noteRequired, entry) = Table[row];
        var result = ComplaintTransitionPolicy.Evaluate(command, from, entry, actor, "gerekçe", "soru");

        Assert.True(result.IsSuccess, $"#{number} reddedildi: {(result.IsFailure ? result.Error.Code : "")}");
        Assert.Equal(number, result.Value.Number);
        Assert.Equal(to, result.Value.To);
        Assert.Equal(reasonRequired, result.Value.ReasonRequired);
        Assert.Equal(noteRequired, result.Value.ReporterNoteRequired);
    }

    [Fact]
    public void Policy_table_has_exactly_the_23_documented_rows()
    {
        Assert.Equal(Enumerable.Range(1, 23), ComplaintTransitionPolicy.Rules.Select(r => r.Number).Distinct().Order());
        Assert.Equal(Table.Length, ComplaintTransitionPolicy.Rules.Count);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Missing_required_reason_or_question_is_rejected(int row)
    {
        var (number, command, from, _, actor, reasonRequired, noteRequired, entry) = Table[row];
        if (!reasonRequired && !noteRequired)
        {
            Assert.True(ComplaintTransitionPolicy.Evaluate(command, from, entry, actor, null, null).IsSuccess, $"#{number}");
            return;
        }

        if (reasonRequired)
        {
            var noReason = ComplaintTransitionPolicy.Evaluate(command, from, entry, actor, "   ", "soru");
            AssertValidation(noReason, "reason");
        }

        if (noteRequired)
        {
            var noQuestion = ComplaintTransitionPolicy.Evaluate(command, from, entry, actor, "gerekçe", null);
            AssertValidation(noQuestion, "reporterNote");
        }
    }

    /// <summary>
    /// Her (durum, komut) çifti: tabloda yoksa <c>complaint.invalid_transition</c>. Böylece her kaynak durumdan (terminal
    /// durumlar dâhil) yasak geçişler kapsanır.
    /// </summary>
    [Fact]
    public void Every_transition_not_in_the_table_is_rejected_from_every_status()
    {
        var forbiddenPerStatus = new Dictionary<S, int>();

        foreach (var status in Enum.GetValues<S>())
        {
            foreach (var command in Enum.GetValues<C>())
            {
                foreach (var entry in new S?[] { null, S.UnderReview, S.InProgress })
                {
                    if (Table.Any(r => r.Command == command && r.From == status && (r.Entry is null || r.Entry == entry)))
                    {
                        continue;
                    }

                    // AwaitingInformation dışında giriş durumu kalıcı veride hiç dolu olmaz; anlamlı olan null'dır.
                    if (status != S.AwaitingInformation && entry is not null)
                    {
                        continue;
                    }

                    var result = ComplaintTransitionPolicy.Evaluate(command, status, entry, ActorRole.Admin, "gerekçe", "soru");
                    var asReporter = ComplaintTransitionPolicy.Evaluate(command, status, entry, ActorRole.Reporter, "gerekçe", "soru");
                    Assert.True(result.IsFailure && asReporter.IsFailure, $"{status} --{command}--> (giriş {entry}) yasak olmalıydı.");
                    Assert.Equal(ComplaintErrorCodes.InvalidTransition, result.Error.Code);
                    Assert.Equal(ErrorType.Conflict, result.Error.Type);
                    forbiddenPerStatus[status] = forbiddenPerStatus.GetValueOrDefault(status) + 1;
                }
            }
        }

        Assert.All(Enum.GetValues<S>(), s => Assert.True(forbiddenPerStatus.GetValueOrDefault(s) > 0, $"{s} için yasak geçiş denenmedi."));
    }

    [Theory]
    [InlineData(S.Closed, S.InProgress)] // dokümandaki örnek: Closed → InProgress yok
    [InlineData(S.Rejected, S.UnderReview)]
    [InlineData(S.Withdrawn, S.UnderReview)]
    public void Terminal_and_closed_records_cannot_move_by_other_commands(S from, S notTo)
    {
        Assert.DoesNotContain(ComplaintTransitionPolicy.Rules, r => r.From == from && r.To == notTo && r.Number != 22);
    }

    [Theory]
    [InlineData(S.UnderReview)]
    [InlineData(S.InProgress)]
    public void Leaving_awaiting_information_goes_only_to_the_entry_status(S entry)
    {
        foreach (var (command, actor) in new[] { (C.Resume, ActorRole.Moderator), (C.ProvideInformation, ActorRole.Reporter) })
        {
            var result = ComplaintTransitionPolicy.Evaluate(command, S.AwaitingInformation, entry, actor, null, null);
            Assert.Equal(entry, result.Value.To);
        }

        // Giriş durumu bilinmiyorsa (kalıcı veri yok) dönüş yapılamaz: istemci hedef seçemez.
        Assert.Equal(ComplaintErrorCodes.InvalidTransition,
            ComplaintTransitionPolicy.Evaluate(C.Resume, S.AwaitingInformation, null, ActorRole.Admin, null, null).Error.Code);
    }

    [Fact]
    public void Wrong_actor_is_forbidden()
    {
        (C, S, ActorRole)[] cases =
        [
            (C.Reopen, S.Closed, ActorRole.Moderator), // #22 yalnızca Admin
            (C.Resume, S.AwaitingInformation, ActorRole.Reporter), // panel komutu
            (C.ProvideInformation, S.AwaitingInformation, ActorRole.Moderator), // şikâyetçi komutu
            (C.Withdraw, S.AwaitingInformation, ActorRole.Admin),
            (C.StartReview, S.Submitted, ActorRole.Reporter),
            (C.SubmitFromIntake, S.Submitted, ActorRole.Admin),
        ];

        foreach (var (command, from, actor) in cases)
        {
            var result = ComplaintTransitionPolicy.Evaluate(command, from == S.Submitted && command == C.SubmitFromIntake ? null : from,
                S.UnderReview, actor, "gerekçe", "soru");
            Assert.True(result.IsFailure, $"{command} / {actor}");
            Assert.Equal(ComplaintErrorCodes.ActionForbidden, result.Error.Code);
            Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        }
    }

    [Fact]
    public void Allowed_actions_follow_the_policy()
    {
        Assert.Equal(["mark-duplicate", "mark-spam", "reject", "start-review"],
            ComplaintTransitionPolicy.AllowedStaffActions(S.Submitted, null, isAdmin: false));
        Assert.Equal(["assign", "start-work", "unassign"], ComplaintTransitionPolicy.AllowedStaffActions(S.Assigned, null, isAdmin: false));
        Assert.Equal(["assign", "request-information", "resolve"], ComplaintTransitionPolicy.AllowedStaffActions(S.InProgress, null, isAdmin: false));
        Assert.Equal(["resume"], ComplaintTransitionPolicy.AllowedStaffActions(S.AwaitingInformation, S.InProgress, isAdmin: false));
        Assert.Empty(ComplaintTransitionPolicy.AllowedStaffActions(S.Closed, null, isAdmin: false));
        Assert.Equal(["reopen"], ComplaintTransitionPolicy.AllowedStaffActions(S.Closed, null, isAdmin: true));
        Assert.All(new[] { S.Rejected, S.Duplicate, S.Spam, S.Withdrawn },
            s => Assert.Empty(ComplaintTransitionPolicy.AllowedStaffActions(s, null, isAdmin: true)));
    }

    private static void AssertValidation(Result<TransitionRule> result, string field)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(ComplaintErrorCodes.ValidationFailed, result.Error.Code);
        Assert.True(result.Error.ValidationErrors!.ContainsKey(field), $"'{field}' hatası bekleniyordu.");
    }
}
