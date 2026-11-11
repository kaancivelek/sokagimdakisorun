using System.Text.RegularExpressions;
using SokagimdakiSorun.Complaints.Contracts;
using SokagimdakiSorun.Complaints.Domain;

namespace SokagimdakiSorun.Complaints.Tests;

/// <summary>Aggregate komutlarının yan etkileri: geçmiş satırı, outbox özeti, atama, giriş durumu, tekrar kuralı.</summary>
public sealed class ComplaintAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly ComplaintActor Moderator = ComplaintActor.Staff(Guid.CreateVersion7(), isAdmin: false);
    private static readonly ComplaintActor Admin = ComplaintActor.Staff(Guid.CreateVersion7(), isAdmin: true);
    private static readonly Guid Unit = Guid.CreateVersion7();

    [Fact]
    public void Submit_records_initial_history_row_and_submitted_change()
    {
        var c = NewComplaint();

        Assert.Equal(ComplaintStatus.Submitted, c.Status);
        var row = Assert.Single(c.History);
        Assert.Null(row.FromStatus);
        Assert.Equal(ActorKind.System, row.ActorKind);
        Assert.Null(row.ActorUserId);
        Assert.Equal(ChangeSource.WhatsApp, row.Source);
        var change = Assert.Single(c.PendingChanges);
        Assert.Null(change.From);
    }

    [Fact]
    public void Each_transition_adds_one_history_row_with_actor_reason_and_note()
    {
        var c = NewComplaint();
        c.ClearPendingChanges();

        Assert.True(c.StartReview(Moderator, null, Now).IsSuccess);
        Assert.True(c.Reject(Moderator, " iç değerlendirme ", "Kapsam dışı.", Now.AddMinutes(1)).IsSuccess);

        Assert.Equal(3, c.History.Count);
        var last = c.History.Last();
        Assert.Equal(ComplaintStatus.UnderReview, last.FromStatus);
        Assert.Equal(ComplaintStatus.Rejected, last.ToStatus);
        Assert.Equal(ActorKind.Staff, last.ActorKind);
        Assert.Equal(Moderator.UserId, last.ActorUserId);
        Assert.Equal(ChangeSource.Panel, last.Source);
        Assert.Equal("iç değerlendirme", last.Reason);
        Assert.Equal("Kapsam dışı.", last.ReporterNote);
        Assert.Equal(2, c.PendingChanges.Count);
        Assert.Equal("Kapsam dışı.", c.PendingChanges[^1].ReporterNote); // olay iç gerekçeyi değil yalnızca notu taşır
    }

    [Fact]
    public void Failed_command_changes_nothing()
    {
        var c = NewComplaint();
        var before = c.History.Count;

        var result = c.Resolve(Moderator, "not", null, Now);

        Assert.Equal(ComplaintErrorCodes.InvalidTransition, result.Error.Code);
        Assert.Equal(ComplaintStatus.Submitted, c.Status);
        Assert.Equal(before, c.History.Count);
        Assert.Null(c.ResolvedAt);
    }

    [Fact]
    public void Request_information_from_review_returns_to_review()
    {
        var c = InState(ComplaintStatus.UnderReview);

        Assert.True(c.RequestInformation(Moderator, "fotoğraf net değil", "Yeni bir fotoğraf gönderebilir misiniz?", Now).IsSuccess);
        Assert.Equal(ComplaintStatus.UnderReview, c.AwaitingInformationEntryStatus);
        Assert.True(c.ProvideInformation(ComplaintActor.Reporter(c.ReporterUserId), Guid.CreateVersion7(), Now).IsSuccess);

        Assert.Equal(ComplaintStatus.UnderReview, c.Status);
        Assert.Null(c.AwaitingInformationEntryStatus);
    }

    [Fact]
    public void Request_information_from_work_returns_to_work_and_keeps_assignment()
    {
        var c = InState(ComplaintStatus.InProgress);
        var assignment = c.ActiveAssignment;
        Assert.NotNull(assignment);

        Assert.True(c.RequestInformation(Moderator, "konum belirsiz", "Tam konumu paylaşır mısınız?", Now).IsSuccess);
        Assert.Same(assignment, c.ActiveAssignment);
        Assert.True(c.Resume(Moderator, null, Now).IsSuccess);

        Assert.Equal(ComplaintStatus.InProgress, c.Status);
        Assert.Same(assignment, c.ActiveAssignment);
        Assert.True(assignment.IsActive);
    }

    [Fact]
    public void Assign_opens_single_active_assignment_and_reassign_closes_previous()
    {
        var c = InState(ComplaintStatus.Accepted);
        Assert.True(c.Assign(Moderator, Unit, null, null, Now).IsSuccess);
        Assert.Equal(ComplaintStatus.Assigned, c.Status);

        // Assigned iken: durum değişmez, geçmiş satırı eklenmez.
        var historyCount = c.History.Count;
        var other = Guid.CreateVersion7();
        Assert.True(c.Assign(Moderator, other, null, null, Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(ComplaintStatus.Assigned, c.Status);
        Assert.Equal(historyCount, c.History.Count);
        Assert.Equal(other, c.ActiveAssignment!.ServiceUnitId);
        Assert.Single(c.Assignments, a => a.IsActive);

        Assert.True(c.StartWork(Moderator, null, null, Now.AddMinutes(2)).IsSuccess);
        Assert.Equal("reason", Assert.Single(c.Reassign(Unit).Error.ValidationErrors!.Keys));
        Assert.True(c.Assign(Moderator, Unit, null, "yanlış birim", Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(ComplaintStatus.Assigned, c.Status);
        Assert.Equal(Unit, c.ActiveAssignment!.ServiceUnitId);
        Assert.Equal(3, c.Assignments.Count);
        Assert.Single(c.Assignments, a => a.IsActive);
    }

    [Fact]
    public void Unassign_and_reopen_close_the_active_assignment()
    {
        var assigned = InState(ComplaintStatus.Assigned);
        Assert.True(assigned.Unassign(Moderator, "birim değişecek", Now).IsSuccess);
        Assert.Null(assigned.ActiveAssignment);
        Assert.Equal(ComplaintStatus.Accepted, assigned.Status);

        var closed = InState(ComplaintStatus.Closed);
        Assert.NotNull(closed.ClosedAt);
        Assert.Equal(ComplaintErrorCodes.ActionForbidden, closed.Reopen(Moderator, "tekrar açılmalı", Now).Error.Code);
        Assert.True(closed.Reopen(Admin, "tekrar açılmalı", Now).IsSuccess);
        Assert.Equal(ComplaintStatus.UnderReview, closed.Status);
        Assert.Null(closed.ActiveAssignment);
        Assert.Null(closed.ClosedAt);
        Assert.Null(closed.ResolvedAt);
    }

    [Fact]
    public void Duplicate_target_cannot_be_self_or_a_duplicate()
    {
        var c = InState(ComplaintStatus.UnderReview);
        var duplicate = InState(ComplaintStatus.Duplicate);
        var valid = NewComplaint();

        Assert.Equal(ComplaintErrorCodes.DuplicateTargetInvalid, c.MarkDuplicate(Moderator, c, "aynı", null, Now).Error.Code);
        Assert.Equal(ComplaintErrorCodes.DuplicateTargetInvalid, c.MarkDuplicate(Moderator, duplicate, "aynı", null, Now).Error.Code);
        Assert.Equal(ComplaintErrorCodes.DuplicateTargetInvalid, c.MarkDuplicate(Moderator, null, "aynı", null, Now).Error.Code);
        Assert.Equal(ComplaintStatus.UnderReview, c.Status);

        Assert.True(c.MarkDuplicate(Moderator, valid, "aynı çukur", null, Now).IsSuccess);
        Assert.Equal(ComplaintStatus.Duplicate, c.Status);
        Assert.Equal(valid.Id, c.DuplicateOfComplaintId);
    }

    [Fact]
    public void Public_reference_has_the_documented_shape_and_is_random()
    {
        var refs = Enumerable.Range(0, 500).Select(_ => PublicReferenceGenerator.Next()).ToList();
        Assert.All(refs, r => Assert.Matches(new Regex(PublicReferenceGenerator.Pattern), r));
        Assert.True(refs.Distinct().Count() > 495);
        Assert.All(refs, r => Assert.DoesNotContain(r[3..], c => c is '0' or 'O' or '1' or 'I' or 'L'));
    }

    internal static Complaint NewComplaint() => Complaint.SubmitFromIntake(
        Guid.CreateVersion7(), PublicReferenceGenerator.Next(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Çukur var",
        40.41, 26.67, null, LocationSource.WhatsAppPin, Now);

    /// <summary>Şikâyeti izinli komutlarla verilen duruma getirir.</summary>
    internal static Complaint InState(ComplaintStatus target)
    {
        var c = NewComplaint();
        var path = target switch
        {
            ComplaintStatus.Submitted => [],
            ComplaintStatus.UnderReview => [ComplaintStatus.UnderReview],
            ComplaintStatus.Accepted => [ComplaintStatus.UnderReview, ComplaintStatus.Accepted],
            ComplaintStatus.Assigned => [ComplaintStatus.UnderReview, ComplaintStatus.Accepted, ComplaintStatus.Assigned],
            ComplaintStatus.InProgress => [ComplaintStatus.UnderReview, ComplaintStatus.Accepted, ComplaintStatus.Assigned, ComplaintStatus.InProgress],
            ComplaintStatus.Resolved => [ComplaintStatus.UnderReview, ComplaintStatus.Accepted, ComplaintStatus.Assigned, ComplaintStatus.InProgress, ComplaintStatus.Resolved],
            ComplaintStatus.Closed => [ComplaintStatus.UnderReview, ComplaintStatus.Accepted, ComplaintStatus.Assigned, ComplaintStatus.InProgress, ComplaintStatus.Resolved, ComplaintStatus.Closed],
            ComplaintStatus.Duplicate => new[] { ComplaintStatus.Duplicate },
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

        foreach (var step in path)
        {
            var result = step switch
            {
                ComplaintStatus.UnderReview => c.StartReview(Admin, null, Now),
                ComplaintStatus.Accepted => c.Accept(Admin, null, Now),
                ComplaintStatus.Assigned => c.Assign(Admin, Unit, null, null, Now),
                ComplaintStatus.InProgress => c.StartWork(Admin, null, null, Now),
                ComplaintStatus.Resolved => c.Resolve(Admin, "giderildi", null, Now),
                ComplaintStatus.Closed => c.Close(Admin, null, null, Now),
                ComplaintStatus.Duplicate => c.MarkDuplicate(Admin, NewComplaint(), "tekrar", null, Now),
                _ => throw new InvalidOperationException(),
            };
            Assert.True(result.IsSuccess, $"{step}: {(result.IsFailure ? result.Error.Code : "")}");
        }

        Assert.Equal(target, c.Status);
        return c;
    }
}

internal static class ComplaintTestExtensions
{
    /// <summary>InProgress'te gerekçesiz yeniden atama (#19 gerekçe zorunlu).</summary>
    public static SharedKernel.Result Reassign(this Complaint complaint, Guid unit) =>
        complaint.Assign(ComplaintActor.Staff(Guid.CreateVersion7(), isAdmin: false), unit, null, null, DateTimeOffset.UnixEpoch);
}
