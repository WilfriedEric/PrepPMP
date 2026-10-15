using Microsoft.EntityFrameworkCore;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure.Services;

namespace PmpStudy.Tests;

public class SeedTests
{
    [Fact]
    public async Task Seed_loads_eco_tasks_and_both_question_banks_without_errors()
    {
        await using var db = await TestDb.CreateAsync();
        await using var ctx = db.CreateDbContext();
        Assert.Equal(26, await ctx.EcoTasks.CountAsync());
        Assert.Equal(50, await ctx.Questions.CountAsync(q => q.Source == "Test prédictif"));
        Assert.Equal(59, await ctx.Questions.CountAsync(q => q.Source == "Examen blanc ch. 1-12"));
        Assert.True(await ctx.Questions.AllAsync(q => q.EcoTaskId != null));
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate()
    {
        await using var db = await TestDb.CreateAsync();
        await new PmpStudy.Infrastructure.DataSeeder(db, db.Content, Microsoft.Extensions.Logging.Abstractions.NullLogger<PmpStudy.Infrastructure.DataSeeder>.Instance).SeedAsync();
        await using var ctx = db.CreateDbContext();
        Assert.Equal(109, await ctx.Questions.CountAsync());
    }

    [Fact]
    public async Task Reimport_with_same_id_updates_in_place()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var q = new QuestionImport
        {
            Id = "t-1", Type = "single", EcoTask = "PE-2", Approach = "agile", Stem = "v1",
            Options = [new() { Text = "a", Correct = true }, new() { Text = "b" }],
        };
        Assert.Equal(1, (await db.Content.ImportQuestionsAsync([q])).Created);
        q.Stem = "v2";
        var second = await db.Content.ImportQuestionsAsync([q]);
        Assert.Equal(1, second.Updated);
        await using var ctx = db.CreateDbContext();
        Assert.Equal("v2", (await ctx.Questions.SingleAsync()).Stem);
    }

    [Fact]
    public async Task Invalid_questions_are_reported_not_imported()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var result = await db.Content.ImportQuestionsAsync(
        [
            new QuestionImport { Stem = "deux bonnes réponses", Options = [new() { Text = "a", Correct = true }, new() { Text = "b", Correct = true }] },
            new QuestionImport { Stem = "tâche inconnue", EcoTask = "XX-9", Options = [new() { Text = "a", Correct = true }, new() { Text = "b" }] },
        ]);
        Assert.Equal(0, result.Created);
        Assert.Equal(2, result.Errors.Count);
    }
}

public class TrainingFlowTests
{
    [Fact]
    public async Task Wrong_and_doubtful_answers_feed_the_error_log()
    {
        await using var db = await TestDb.CreateAsync();
        var id = await db.Sessions.CreateTrainingAsync(new TrainingRequest { Count = 3, FeedbackMode = FeedbackMode.AtEnd });
        var session = (await db.Sessions.GetAsync(id))!;
        var items = session.Items.OrderBy(i => i.Order).ToList();

        List<int> Right(SessionItem i) => i.Question!.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToList();
        int Wrong(SessionItem i) => i.Question!.Options.First(o => !o.IsCorrect).Id;

        Assert.True((await db.Sessions.AnswerAsync(id, items[0].Id, Right(items[0]), 30)).Accepted);
        Assert.True((await db.Sessions.AnswerAsync(id, items[1].Id, [Wrong(items[1])], 40)).Accepted);
        Assert.Null(await db.Sessions.SetMarksAsync(id, items[0].Id, flagged: false, doubt: true));
        // items[2] reste sans réponse : compte comme fausse.

        var done = (await db.Sessions.FinishAsync(id))!;
        Assert.Equal(1, done.CorrectCount);
        Assert.Equal(33.3, done.ScorePercent);

        var log = await db.Errors.ListAsync(ErrorFilter.All);
        Assert.Equal(3, log.Count); // fausse + sans réponse + juste avec doute
        Assert.All(log, e => Assert.Equal(db.Clock.Today.AddDays(1), e.NextReview));
        Assert.All(log, e => Assert.True(e.IsIncomplete));
    }

    [Fact]
    public async Task Immediate_feedback_reveals_correctness_and_blocks_second_answer()
    {
        await using var db = await TestDb.CreateAsync();
        var id = await db.Sessions.CreateTrainingAsync(new TrainingRequest { Count = 1, FeedbackMode = FeedbackMode.Immediate, EcoTasks = ["PR-9"] });
        var item = (await db.Sessions.GetAsync(id))!.Items.Single();
        var correct = item.Question!.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToList();

        var first = await db.Sessions.AnswerAsync(id, item.Id, correct, 10);
        Assert.True(first.IsCorrect);
        var second = await db.Sessions.AnswerAsync(id, item.Id, correct, 10);
        Assert.False(second.Accepted);
    }

    [Fact]
    public async Task Server_closes_expired_sessions_and_rejects_late_answers()
    {
        await using var db = await TestDb.CreateAsync();
        var id = await db.Sessions.CreateTrainingAsync(new TrainingRequest { Count = 2, SecondsPerQuestion = 60 });
        var item = (await db.Sessions.GetAsync(id))!.Items.First();

        db.Time.Advance(TimeSpan.FromMinutes(3));
        var late = await db.Sessions.AnswerAsync(id, item.Id, [item.Question!.Options[0].Id], 5);
        Assert.False(late.Accepted);
        Assert.True(late.SessionFinished);

        var s = (await db.Sessions.GetAsync(id))!;
        Assert.Equal(s.StartUtc.AddMinutes(2), s.EndUtc); // clôturée à l'échéance, pas à l'heure de la requête
        Assert.Equal(0, s.CorrectCount);
    }

    [Fact]
    public async Task Replay_success_twice_masters_the_entry()
    {
        await using var db = await TestDb.CreateAsync();
        var id = await db.Sessions.CreateTrainingAsync(new TrainingRequest { Count = 1, FeedbackMode = FeedbackMode.AtEnd });
        await db.Sessions.FinishAsync(id); // sans réponse → erreur
        Assert.Equal(0, await db.Errors.CountDueAsync());

        for (var round = 0; round < 2; round++)
        {
            db.Time.Advance(TimeSpan.FromDays(round == 0 ? 1 : 3));
            Assert.Equal(1, await db.Errors.CountDueAsync());
            var replay = await db.Sessions.CreateErrorReplayAsync(dueOnly: true);
            var item = (await db.Sessions.GetAsync(replay))!.Items.Single();
            await db.Sessions.AnswerAsync(replay, item.Id, item.Question!.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToList(), 20);
            await db.Sessions.FinishAsync(replay);
        }

        var entry = (await db.Errors.ListAsync(ErrorFilter.All)).Single();
        Assert.Equal(ErrorStatus.Mastered, entry.Status);
    }
}

public class ExamFlowTests
{
    [Fact]
    public async Task Exam_sections_lock_breaks_extend_deadline_and_scoring_waits_for_the_end()
    {
        await using var db = await TestDb.CreateAsync();
        var (id, count) = await db.Sessions.CreateExamAsync(9);
        Assert.Equal(9, count);
        var exam = (await db.Sessions.GetAsync(id))!;
        Assert.Equal(3, exam.Sections.Count);
        Assert.Equal(TimeSpan.FromSeconds(9 * 80), exam.DeadlineUtc - exam.StartUtc);

        var s1 = exam.Items.Where(i => i.SectionIndex == 1).ToList();
        var s2 = exam.Items.Where(i => i.SectionIndex == 2).ToList();
        Assert.Equal(3, s1.Count);

        // Pas de correction pendant l'examen.
        var answer = await db.Sessions.AnswerAsync(id, s1[0].Id, s1[0].Question!.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToList(), 30);
        Assert.True(answer.Accepted);
        Assert.Null(answer.IsCorrect);

        // Section 2 inaccessible tant que la 1 n'est pas validée.
        Assert.False((await db.Sessions.AnswerAsync(id, s2[0].Id, [s2[0].Question!.Options[0].Id], 5)).Accepted);

        Assert.Null(await db.Sessions.SubmitSectionAsync(id, 1));
        Assert.False((await db.Sessions.AnswerAsync(id, s1[1].Id, [s1[1].Question!.Options[0].Id], 5)).Accepted); // verrouillée

        var before = SessionClock.EffectiveDeadline((await db.Sessions.GetAsync(id))!, db.Clock.UtcNow);
        Assert.Null(await db.Sessions.StartBreakAsync(id, 1));
        Assert.NotNull(await db.Sessions.StartBreakAsync(id, 1)); // une seule pause
        db.Time.Advance(TimeSpan.FromMinutes(12));
        Assert.Null(await db.Sessions.StartNextSectionAsync(id));
        var after = SessionClock.EffectiveDeadline((await db.Sessions.GetAsync(id))!, db.Clock.UtcNow);
        Assert.Equal(TimeSpan.FromMinutes(10), after - before); // 2 min de dépassement non créditées

        Assert.True((await db.Sessions.AnswerAsync(id, s2[0].Id, [s2[0].Question!.Options[0].Id], 5)).Accepted);
        Assert.Null(await db.Sessions.SubmitSectionAsync(id, 2));
        Assert.Null(await db.Sessions.StartNextSectionAsync(id));
        Assert.Null(await db.Sessions.SubmitSectionAsync(id, 3));

        var done = (await db.Sessions.GetAsync(id))!;
        Assert.True(done.IsFinished);
        Assert.Equal(9, done.Items.Count(i => i.IsCorrect is not null));
        Assert.InRange(done.CorrectCount!.Value, 1, 2);
    }

    [Fact]
    public async Task Weekly_report_renders_markdown()
    {
        await using var db = await TestDb.CreateAsync();
        await db.Content.ImportPlanAsync([new PlanDayImport { Date = db.Clock.Today, Week = 1, PageFrom = 1, PageTo = 10, QuestionGoal = 2, EcoTasks = ["PR-9"] }]);
        var id = await db.Sessions.CreateTrainingAsync(new TrainingRequest { Count = 2 });
        await db.Sessions.FinishAsync(id);
        await db.Study.TrackReadingAsync(null, 1, 10, 25);

        var md = await db.Reports.WeeklyMarkdownAsync(1);
        Assert.Contains("# Bilan PMP — semaine 1", md);
        Assert.Contains("Carnet d'erreurs", md);

        var today = await db.Study.TodayAsync();
        Assert.True(today.PagesDone);
    }
}
