using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Infrastructure.Data;

namespace PmpStudy.Infrastructure.Services;

public static class SettingKeys
{
    public const string ExamDate = "ExamDate";
    public const string SimulationDates = "SimulationDates";
    public const string WeeklyHoursTarget = "WeeklyHoursTarget";
    public const string LastPage = "LastPage";
    /// <summary>Décalage entre les pages imprimées (celles du plan) et les pages du PDF.</summary>
    public const string PageOffset = "PageOffset";
    public const string PdfFileName = "PdfFileName";
    public const string PdfUploadedUtc = "PdfUploadedUtc";
    public const string PdfPageCount = "PdfPageCount";
}

public record PageRange(int From, int To);

public class TodaySummary
{
    public DateOnly Date { get; init; }
    public StudyPlanDay? PlanDay { get; init; }
    public List<StudyPlanDay> Week { get; init; } = [];
    public DateOnly ExamDate { get; init; }
    public int DaysToExam { get; init; }
    public DateOnly? NextSimulation { get; init; }
    public int QuestionsAnsweredToday { get; init; }
    public int CorrectToday { get; init; }
    public double ReadingMinutesToday { get; init; }
    public List<PageRange> PagesReadToday { get; init; } = [];
    public bool PagesDone { get; init; }
    public bool QuestionsDone { get; init; }
    public int ErrorsDue { get; init; }
    public int ErrorsIncomplete { get; init; }
    public int? LastPage { get; init; }
    public int PageOffset { get; init; }
    public StudySession? OpenSession { get; init; }
}

public class StudyService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock)
{
    public static readonly DateOnly DefaultExamDate = new(2026, 12, 18);
    public const string DefaultSimulations = "2026-11-28";
    public const double DefaultWeeklyHours = 11;

    // ---------- Réglages ----------

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Settings.FindAsync([key], ct))?.Value;
    }

    public async Task<Dictionary<string, string>> AllSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value, ct);
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.FindAsync([key], ct);
        if (value is null) { if (setting is not null) db.Settings.Remove(setting); }
        else if (setting is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else setting.Value = value;
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateOnly> ExamDateAsync(CancellationToken ct = default) =>
        DateOnly.TryParse(await GetSettingAsync(SettingKeys.ExamDate, ct), CultureInfo.InvariantCulture, out var d) ? d : DefaultExamDate;

    public async Task<List<DateOnly>> SimulationDatesAsync(CancellationToken ct = default) =>
        ((await GetSettingAsync(SettingKeys.SimulationDates, ct)) ?? DefaultSimulations)
            .Split(',', ';', ' ', '\n').Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => DateOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>().Order().ToList();

    public async Task<int> PageOffsetAsync(CancellationToken ct = default) =>
        int.TryParse(await GetSettingAsync(SettingKeys.PageOffset, ct), out var o) ? o : 0;

    // ---------- Lecture ----------

    /// <summary>Enregistre une séance de lecture (créée à l'ouverture du lecteur, mise à jour régulièrement).</summary>
    public async Task<int> TrackReadingAsync(int? readingLogId, int pageFrom, int pageTo, double minutes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var today = clock.Today;
        ReadingLog? log = readingLogId is { } id ? await db.ReadingLogs.FindAsync([id], ct) : null;
        if (log is null || log.Date != today)
        {
            var planDay = await db.PlanDays.FirstOrDefaultAsync(d => d.Date == today, ct);
            log = new ReadingLog { Date = today, StartedUtc = now, PageFrom = pageFrom, PageTo = pageTo, StudyPlanDayId = planDay?.Id };
            db.ReadingLogs.Add(log);
        }
        log.PageFrom = Math.Min(log.PageFrom, pageFrom);
        log.PageTo = Math.Max(log.PageTo, pageTo);
        log.Minutes = Math.Max(log.Minutes, Math.Round(minutes, 1));
        log.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return log.Id;
    }

    public async Task SetLastPageAsync(int page, CancellationToken ct = default) =>
        await SetSettingAsync(SettingKeys.LastPage, page.ToString(CultureInfo.InvariantCulture), ct);

    // ---------- Aujourd'hui ----------

    public async Task<TodaySummary> TodayAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = clock.Today;
        var (startUtc, endUtc) = clock.DayBoundsUtc(today);
        var settings = await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var examDate = settings.TryGetValue(SettingKeys.ExamDate, out var e) && DateOnly.TryParse(e, CultureInfo.InvariantCulture, out var ed) ? ed : DefaultExamDate;
        var simulations = (settings.GetValueOrDefault(SettingKeys.SimulationDates) ?? DefaultSimulations)
            .Split(',', ';', ' ').Select(s => DateOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>().Where(d => d >= today).Order().ToList();
        var offset = settings.TryGetValue(SettingKeys.PageOffset, out var o) && int.TryParse(o, out var oi) ? oi : 0;

        var planDay = await db.PlanDays.Include(d => d.Chapters).Include(d => d.EcoTasks).AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Date == today, ct);
        var week = planDay is null
            ? await WeekAround(db, today, ct)
            : await db.PlanDays.Where(d => d.Week == planDay.Week).OrderBy(d => d.Date).ToListAsync(ct);

        var answered = await db.SessionItems.Where(i => i.AnsweredUtc >= startUtc && i.AnsweredUtc < endUtc)
            .Select(i => new { i.IsCorrect, Finished = i.StudySession!.EndUtc != null, i.StudySession.Type })
            .ToListAsync(ct);
        var logs = await db.ReadingLogs.Where(l => l.Date == today).ToListAsync(ct);
        var ranges = MergeRanges(logs.Select(l => new PageRange(l.PageFrom, l.PageTo)));

        var pagesDone = planDay?.PageFrom is not { } from || planDay.PageTo is not { } to
            || Covers(ranges, from + offset, to + offset);
        var questionsDone = planDay is null || answered.Count >= planDay.QuestionGoal;

        if (planDay is not null && pagesDone && questionsDone && planDay.Status != PlanDayStatus.Done)
        {
            planDay.Status = PlanDayStatus.Done;
            await db.SaveChangesAsync(ct);
        }
        else if (planDay is not null && planDay.Status == PlanDayStatus.Todo && (answered.Count > 0 || logs.Count > 0))
        {
            planDay.Status = PlanDayStatus.InProgress;
            await db.SaveChangesAsync(ct);
        }

        var now = clock.UtcNow;
        var open = await db.Sessions.Include(s => s.Sections).Where(s => s.EndUtc == null).OrderByDescending(s => s.StartUtc).ToListAsync(ct);

        return new TodaySummary
        {
            Date = today,
            PlanDay = planDay,
            Week = week,
            ExamDate = examDate,
            DaysToExam = examDate.DayNumber - today.DayNumber,
            NextSimulation = simulations.FirstOrDefault() is var s && s != default ? s : null,
            // Seules les sessions corrigées révèlent le score du jour (un examen en cours reste caché).
            QuestionsAnsweredToday = answered.Count,
            CorrectToday = answered.Count(a => a.IsCorrect == true && (a.Finished || a.Type != SessionType.Exam)),
            ReadingMinutesToday = logs.Sum(l => l.Minutes),
            PagesReadToday = ranges,
            PagesDone = pagesDone,
            QuestionsDone = questionsDone,
            ErrorsDue = await db.ErrorEntries.CountAsync(x => x.Status == ErrorStatus.Active && x.NextReview <= today, ct),
            ErrorsIncomplete = await db.ErrorEntries.CountAsync(x => x.Why == null || x.Why == "" || x.Trap == null || x.Trap == "", ct),
            LastPage = settings.TryGetValue(SettingKeys.LastPage, out var lp) && int.TryParse(lp, out var lpi) ? lpi : null,
            PageOffset = offset,
            OpenSession = open.FirstOrDefault(x => !Core.Rules.SessionClock.IsExpired(x, now)),
        };
    }

    private static async Task<List<StudyPlanDay>> WeekAround(AppDbContext db, DateOnly today, CancellationToken ct)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var sunday = monday.AddDays(6);
        return await db.PlanDays.Where(d => d.Date >= monday && d.Date <= sunday).OrderBy(d => d.Date).ToListAsync(ct);
    }

    public async Task SetDayStatusAsync(int planDayId, PlanDayStatus status, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PlanDays.Where(d => d.Id == planDayId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status), ct);
    }

    public static List<PageRange> MergeRanges(IEnumerable<PageRange> ranges)
    {
        var result = new List<PageRange>();
        foreach (var r in ranges.Select(r => r.From <= r.To ? r : new PageRange(r.To, r.From)).OrderBy(r => r.From))
        {
            if (result.Count > 0 && r.From <= result[^1].To + 1)
                result[^1] = result[^1] with { To = Math.Max(result[^1].To, r.To) };
            else result.Add(r);
        }
        return result;
    }

    public static bool Covers(IReadOnlyList<PageRange> merged, int from, int to) =>
        merged.Any(r => r.From <= from && r.To >= to);
}
