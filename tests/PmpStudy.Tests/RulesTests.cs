using PmpStudy.Core.Entities;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure.Services;

namespace PmpStudy.Tests;

public class SpacedRepetitionTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly DateTime Now = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_error_is_due_the_next_day()
    {
        var e = SpacedRepetition.CreateFromMiss(Today, Now);
        Assert.Equal(Today.AddDays(1), e.NextReview);
        Assert.False(SpacedRepetition.IsDue(e, Today));
        Assert.True(SpacedRepetition.IsDue(e, Today.AddDays(1)));
    }

    [Fact]
    public void Correct_review_moves_to_J3_then_two_in_a_row_masters()
    {
        var e = SpacedRepetition.CreateFromMiss(Today, Now);
        SpacedRepetition.RecordSuccess(e, Today.AddDays(1));
        Assert.Equal(ErrorStatus.Active, e.Status);
        Assert.Equal(Today.AddDays(1 + 3), e.NextReview);

        SpacedRepetition.RecordSuccess(e, Today.AddDays(4));
        Assert.Equal(ErrorStatus.Mastered, e.Status);
        Assert.Equal(2, e.ReviewCount);
    }

    [Fact]
    public void Miss_resets_to_J1_and_breaks_the_streak()
    {
        var e = SpacedRepetition.CreateFromMiss(Today, Now);
        SpacedRepetition.RecordSuccess(e, Today.AddDays(1));
        SpacedRepetition.RecordMiss(e, Today.AddDays(4), isReview: true);
        Assert.Equal(0, e.ConsecutiveCorrect);
        Assert.Equal(0, e.IntervalStep);
        Assert.Equal(Today.AddDays(5), e.NextReview);
        Assert.Equal(2, e.TimesWrong);
    }

    [Fact]
    public void Incomplete_until_why_and_trap_are_filled()
    {
        var e = new ErrorEntry { Why = "confusion EV/PV" };
        Assert.True(e.IsIncomplete);
        e.Trap = "« valeur des 40 % » = EV";
        Assert.False(e.IsIncomplete);
    }
}

public class SessionClockTests
{
    private static readonly DateTime Start = new(2026, 11, 28, 8, 0, 0, DateTimeKind.Utc);

    private static StudySession Exam() => new()
    {
        Type = SessionType.Exam,
        StartUtc = Start,
        DeadlineUtc = Start + SessionClock.ExamDuration,
        Sections = [new() { Index = 1, StartUtc = Start }, new() { Index = 2 }, new() { Index = 3 }],
    };

    [Fact]
    public void Break_under_ten_minutes_does_not_count()
    {
        var s = Exam();
        s.Sections[0].EndUtc = Start.AddMinutes(70);
        s.Sections[0].BreakStartUtc = Start.AddMinutes(70);
        s.Sections[0].BreakEndUtc = Start.AddMinutes(76);
        Assert.Equal(Start.AddMinutes(246), SessionClock.EffectiveDeadline(s, Start.AddMinutes(100)));
    }

    [Fact]
    public void Break_overrun_is_deducted_from_remaining_time()
    {
        var s = Exam();
        s.Sections[0].BreakStartUtc = Start.AddMinutes(70);
        s.Sections[0].BreakEndUtc = Start.AddMinutes(85); // 15 min : 5 de trop
        Assert.Equal(Start.AddMinutes(250), SessionClock.EffectiveDeadline(s, Start.AddMinutes(100)));
        Assert.Equal(TimeSpan.FromMinutes(150), SessionClock.Remaining(s, Start.AddMinutes(100)));
    }

    [Fact]
    public void Remaining_time_is_frozen_during_the_first_ten_minutes_of_a_break()
    {
        var s = Exam();
        s.Sections[0].BreakStartUtc = Start.AddMinutes(70);
        var atBreak = SessionClock.Remaining(s, Start.AddMinutes(70));
        Assert.Equal(atBreak, SessionClock.Remaining(s, Start.AddMinutes(79)));
        Assert.Equal(atBreak - TimeSpan.FromMinutes(2), SessionClock.Remaining(s, Start.AddMinutes(82)));
    }

    [Fact]
    public void Expired_after_deadline()
    {
        var s = Exam();
        Assert.False(SessionClock.IsExpired(s, Start.AddMinutes(239)));
        Assert.True(SessionClock.IsExpired(s, Start.AddMinutes(240)));
    }
}

public class ExamComposerTests
{
    [Fact]
    public void Targets_match_the_2026_eco_split()
    {
        var t = ExamComposer.DomainTargets(180);
        Assert.Equal(59, t[Domain.People]);
        Assert.Equal(74, t[Domain.Process]);
        Assert.Equal(47, t[Domain.BusinessEnvironment]);
    }

    [Fact]
    public void Full_bank_gives_180_unique_questions_with_proportions()
    {
        var id = 0;
        var pool = new List<ExamCandidate>();
        foreach (var d in Enum.GetValues<Domain>())
            foreach (var a in Enum.GetValues<Approach>())
                for (var i = 0; i < 80; i++) pool.Add(new(++id, d, a, 0));
        var byId = pool.ToDictionary(c => c.QuestionId);

        var picked = ExamComposer.Compose(pool, 180, new Random(42));

        Assert.Equal(180, picked.Count);
        Assert.Equal(180, picked.Distinct().Count());
        Assert.Equal(59, picked.Count(p => byId[p].Domain == Domain.People));
        Assert.Equal(74, picked.Count(p => byId[p].Domain == Domain.Process));
        Assert.Equal(47, picked.Count(p => byId[p].Domain == Domain.BusinessEnvironment));
        var predictive = picked.Count(p => byId[p].Approach == Approach.Predictive);
        Assert.InRange(predictive, 70, 74); // ≈ 40 %
    }

    [Fact]
    public void Small_bank_uses_everything_and_prefers_unseen()
    {
        var pool = new List<ExamCandidate>
        {
            new(1, Domain.People, Approach.Agile, 3),
            new(2, Domain.People, Approach.Agile, 0),
            new(3, Domain.Process, Approach.Predictive, 0),
            new(4, null, Approach.Predictive, 0),
        };
        Assert.Equal(4, ExamComposer.Compose(pool, 180, new Random(1)).Count);
        var two = ExamComposer.Compose(pool, 2, new Random(1));
        Assert.DoesNotContain(1, two);
    }
}

public class ScoringTests
{
    private static Question Q(QuestionType type, params bool[] correct) => new()
    {
        Type = type,
        Options = correct.Select((c, i) => new QuestionOption { Id = i + 1, IsCorrect = c }).ToList(),
    };

    [Fact]
    public void Single_choice()
    {
        var q = Q(QuestionType.Single, false, true, false);
        Assert.True(Scoring.IsCorrect(q, [2]));
        Assert.False(Scoring.IsCorrect(q, [1]));
        Assert.False(Scoring.IsCorrect(q, []));
    }

    [Fact]
    public void Multiple_choice_requires_the_exact_set()
    {
        var q = Q(QuestionType.Multiple, true, false, true, true);
        Assert.True(Scoring.IsCorrect(q, [1, 3, 4]));
        Assert.False(Scoring.IsCorrect(q, [1, 3]));
        Assert.False(Scoring.IsCorrect(q, [1, 2, 3, 4]));
    }

    [Theory]
    [InlineData(85, PerformanceLevel.AboveTarget)]
    [InlineData(80, PerformanceLevel.AboveTarget)]
    [InlineData(72, PerformanceLevel.Target)]
    [InlineData(65, PerformanceLevel.BelowTarget)]
    [InlineData(40, PerformanceLevel.NeedsImprovement)]
    public void Internal_levels(double percent, PerformanceLevel expected) => Assert.Equal(expected, Scoring.LevelFor(percent));

    [Fact]
    public void Answer_roundtrip_is_sorted_and_distinct()
    {
        Assert.Equal("[3,7]", Scoring.SerializeAnswer([7, 3, 7]));
        Assert.Equal([3, 7], Scoring.ParseAnswer("[3,7]"));
        Assert.Empty(Scoring.ParseAnswer("garbage"));
    }
}

public class PageRangeTests
{
    [Fact]
    public void Ranges_merge_and_cover()
    {
        var merged = StudyService.MergeRanges([new(10, 15), new(16, 20), new(30, 31), new(12, 14)]);
        Assert.Equal([new PageRange(10, 20), new PageRange(30, 31)], merged);
        Assert.True(StudyService.Covers(merged, 11, 20));
        Assert.False(StudyService.Covers(merged, 18, 31));
    }
}

public class EcoTaskGroupingTests
{
    [Fact]
    public void Same_task_loaded_twice_is_grouped_once()
    {
        SessionItem Item(bool ok) => new() { IsCorrect = ok, Question = new Question { EcoTask = new EcoTask { Code = "PR-5", Domain = Domain.Process } } };
        var lines = Scoring.ByEcoTask([Item(true), Item(false)]);
        Assert.Single(lines);
        Assert.Equal(2, lines[0].Total);
    }
}
