using Microsoft.EntityFrameworkCore;
using PmpStudy.Web.Model;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Model.Rules;
using PmpStudy.Web.Services;

namespace PmpStudy.Tests;

public class PlanGeneratorTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static PlanSettings Settings(PlanUnit unit = PlanUnit.Pages, int from = 1, int to = 100, DateOnly? end = null, params DayOfWeek[] rest) =>
        new(Monday, end ?? Monday.AddDays(13), unit, from, to, rest, SkipSimulations: true, QuestionGoal: 30);

    [Fact]
    public void Pages_are_spread_evenly_without_gaps_over_reading_days()
    {
        var plan = PlanGenerator.Generate(Settings(rest: DayOfWeek.Sunday), Monday, Monday.AddDays(30), [], [], resumePage: 1);

        var reading = plan.Days.Where(d => d.Kind == PlanDayKind.Reading).ToList();
        Assert.Equal(12, plan.ReadingDays);                       // 14 jours moins 2 dimanches
        Assert.Equal(12, reading.Count);
        Assert.DoesNotContain(plan.Days, d => d.Date.DayOfWeek == DayOfWeek.Sunday);
        Assert.Equal(1, reading[0].PageFrom);
        Assert.Equal(100, reading[^1].PageTo);
        for (var i = 1; i < reading.Count; i++) Assert.Equal(reading[i - 1].PageTo + 1, reading[i].PageFrom);
        Assert.All(reading, d => Assert.InRange(d.PageTo!.Value - d.PageFrom!.Value + 1, 8, 9));
        // Après la fin de lecture, jusqu'à la veille de l'examen : révision.
        Assert.All(plan.Days.Where(d => d.Date > Monday.AddDays(13)), d => Assert.Equal(PlanDayKind.Review, d.Kind));
        Assert.Equal(Monday.AddDays(29), plan.Days[^1].Date);
    }

    [Fact]
    public void Plan_resumes_at_the_first_unread_page_and_starts_today()
    {
        var today = Monday.AddDays(4);
        var plan = PlanGenerator.Generate(Settings(), today, Monday.AddDays(30), [], [], resumePage: 61);

        Assert.Equal(today, plan.FirstDay);
        Assert.Equal(40, plan.RemainingPages);
        Assert.Equal(10, plan.ReadingDays);
        Assert.Equal(61, plan.Days.First(d => d.Kind == PlanDayKind.Reading).PageFrom);
        Assert.Equal(4, plan.PagesPerDay);
    }

    [Fact]
    public void Simulation_days_replace_reading()
    {
        var sim = Monday.AddDays(2);
        var plan = PlanGenerator.Generate(Settings(), Monday, Monday.AddDays(30), [sim], [], resumePage: 1);

        var day = plan.Days.Single(d => d.Date == sim);
        Assert.Equal(PlanDayKind.Simulation, day.Kind);
        Assert.Equal(PlanGenerator.SimulationQuestions, day.QuestionGoal);
        Assert.Equal(13, plan.ReadingDays);
    }

    [Fact]
    public void Sections_stay_whole_in_order_and_are_balanced_by_pages()
    {
        var sections = new List<PlanSection>();
        var page = 1;
        for (var i = 1; i <= 20; i++)
        {
            var size = i % 3 == 0 ? 12 : 4;                         // tailles inégales
            sections.Add(new PlanSection(i, $"{i}.1", $"Section {i}", page, page + size - 1));
            page += size;
        }
        var plan = PlanGenerator.Generate(Settings(PlanUnit.Sections, 1, page - 1, Monday.AddDays(9)), Monday, Monday.AddDays(20), [], sections, 1);

        var reading = plan.Days.Where(d => d.Kind == PlanDayKind.Reading).ToList();
        var assigned = reading.SelectMany(d => d.Sections).Select(s => s.ChapterId).ToList();
        Assert.Equal(Enumerable.Range(1, 20), assigned);            // toutes, une fois, dans l'ordre
        Assert.Equal(20, plan.RemainingSections);
        Assert.Equal(2, plan.SectionsPerDay);
        var pagesPerDay = reading.Select(d => d.Sections.Sum(s => s.Pages)).ToList();
        Assert.True(pagesPerDay.Max() - pagesPerDay.Min() <= 12, string.Join(",", pagesPerDay));
        Assert.StartsWith("1.1 Section 1", reading[0].Label);
    }

    [Fact]
    public void Long_sections_are_split_into_daily_parts_without_gaps()
    {
        var sections = new List<PlanSection>
        {
            new(1, "1", "Introduction", 1, 40),
            new(2, "2", "Gouvernance", 41, 80),
            new(3, "3", "Risques", 81, 120),
        };
        var plan = PlanGenerator.Generate(Settings(PlanUnit.Sections, 1, 120, Monday.AddDays(11)), Monday, Monday.AddDays(20), [], sections, 1);

        var reading = plan.Days.Where(d => d.Date <= Monday.AddDays(11)).ToList();
        Assert.All(reading, d => Assert.Equal(PlanDayKind.Reading, d.Kind));     // aucun jour vide au milieu
        Assert.Equal("1 Introduction (1/4)", reading[0].Label);
        Assert.Equal((1, 10), (reading[0].PageFrom, reading[0].PageTo));
        Assert.Equal(120, reading[^1].PageTo);
        for (var i = 1; i < reading.Count; i++) Assert.Equal(reading[i - 1].PageTo + 1, reading[i].PageFrom);
    }

    [Fact]
    public void Warns_when_no_reading_day_is_left()
    {
        var plan = PlanGenerator.Generate(Settings(end: Monday.AddDays(-1)), Monday, Monday.AddDays(10), [], [], 1);
        Assert.Equal(0, plan.ReadingDays);
        Assert.NotEmpty(plan.Warnings);
    }
}

public class PlanServiceTests
{
    [Fact]
    public async Task Plan_is_saved_generated_and_recalculated_each_day_from_reading()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var today = db.Clock.Today;
        await db.Study.SetSettingAsync(SettingKeys.ExamDate, today.AddDays(30).ToString("yyyy-MM-dd"));
        await db.Study.SetSettingAsync(SettingKeys.SimulationDates, "");
        var plans = db.Plans;
        var config = await plans.GetConfigAsync();
        config.Start = today;
        config.End = today.AddDays(9);
        config.Unit = PlanUnit.Pages;
        config.FromPage = 1;
        config.ToPage = 100;
        config.RestDays = [];

        var summary = await plans.SaveAndGenerateAsync(config);
        Assert.Equal(10, summary.PagesPerDay);
        var days = await db.Content.PlanAsync();
        Assert.Equal(30, days.Count);
        Assert.Equal((1, 10), (days[0].PageFrom, days[0].PageTo));

        // Le lendemain, 25 pages ont été lues : le reste (75) est réparti sur les 9 jours restants.
        await db.Study.TrackReadingAsync(null, 1, 25, 60);
        db.Time.Advance(TimeSpan.FromDays(1));
        var summaryToday = await new StudyService(db, db.Clock, null, db.Plans).TodayAsync();
        Assert.Equal(26, summaryToday.PlanDay!.PageFrom);
        Assert.Equal(26 + 75 / 9 - 1, summaryToday.PlanDay.PageTo);
        var after = await db.Content.PlanAsync();
        Assert.Equal((1, 10), (after[0].PageFrom, after[0].PageTo));   // le jour passé ne bouge pas
        Assert.Equal(100, after.Where(d => d.PageTo != null).Max(d => d.PageTo));
    }
}
