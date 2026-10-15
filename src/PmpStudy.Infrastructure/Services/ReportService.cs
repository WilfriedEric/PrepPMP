using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure.Data;

namespace PmpStudy.Infrastructure.Services;

public record ExamHistoryLine(int SessionId, string Title, DateTime StartUtc, int Questions, double Score, IReadOnlyList<ScoreLine> ByDomain);
public record WeekHours(int Week, DateOnly From, DateOnly To, double ReadingHours, double QuestionHours, double PlannedHours)
{
    public double DoneHours => Math.Round(ReadingHours + QuestionHours, 1);
}

public class StatsReport
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public int Answered { get; init; }
    public int Correct { get; init; }
    public double Percent => Answered == 0 ? 0 : Math.Round(100.0 * Correct / Answered, 1);
    public double AverageSecondsPerQuestion { get; init; }
    public IReadOnlyList<ScoreLine> ByDomain { get; init; } = [];
    public IReadOnlyList<ScoreLine> ByEcoTask { get; init; } = [];
    public IReadOnlyList<ScoreLine> ByApproach { get; init; } = [];
    public IReadOnlyList<ExamHistoryLine> Exams { get; init; } = [];
    public IReadOnlyList<WeekHours> Hours { get; init; } = [];
}

public class ReportService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock, StudyService study)
{
    /// <summary>Statistiques sur les sessions terminées (tous types), éventuellement sur une période.</summary>
    public async Task<StatsReport> StatsAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.SessionItems.AsNoTracking()
            .Include(i => i.Question).ThenInclude(q => q!.EcoTask)
            .Where(i => i.StudySession!.EndUtc != null && (i.AnsweredUtc != null || i.StudySession.Type == SessionType.Exam));
        if (from is { } f) { var start = clock.DayBoundsUtc(f).StartUtc; query = query.Where(i => i.StudySession!.StartUtc >= start); }
        if (to is { } t) { var end = clock.DayBoundsUtc(t).EndUtc; query = query.Where(i => i.StudySession!.StartUtc < end); }
        var items = await query.ToListAsync(ct);

        var exams = await db.Sessions.AsNoTracking()
            .Include(s => s.Items).ThenInclude(i => i.Question).ThenInclude(q => q!.EcoTask)
            .Where(s => s.Type == SessionType.Exam && s.EndUtc != null)
            .OrderBy(s => s.StartUtc).AsSplitQuery().ToListAsync(ct);

        return new StatsReport
        {
            From = from,
            To = to,
            Answered = items.Count,
            Correct = items.Count(i => i.IsCorrect == true),
            AverageSecondsPerQuestion = items.Count == 0 ? 0 : Math.Round(items.Average(i => i.TimeSeconds), 1),
            ByDomain = Scoring.ByDomain(items),
            ByEcoTask = Scoring.ByEcoTask(items),
            ByApproach = Scoring.ByApproach(items),
            Exams = exams.Select(s => new ExamHistoryLine(s.Id, s.Title ?? "Examen", s.StartUtc, s.Items.Count, s.ScorePercent ?? 0, Scoring.ByDomain(s.Items))).ToList(),
            Hours = await HoursByWeekAsync(db, ct),
        };
    }

    private async Task<List<WeekHours>> HoursByWeekAsync(AppDbContext db, CancellationToken ct)
    {
        var weeklyTarget = double.TryParse(await study.GetSettingAsync(SettingKeys.WeeklyHoursTarget, ct), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            ? w : StudyService.DefaultWeeklyHours;
        var weeks = await WeeksAsync(db, ct);
        var result = new List<WeekHours>();
        foreach (var (week, from, to) in weeks)
        {
            var (readingH, questionH) = await HoursAsync(db, from, to, ct);
            result.Add(new WeekHours(week, from, to, readingH, questionH, weeklyTarget));
        }
        return result;
    }

    private async Task<(double Reading, double Questions)> HoursAsync(AppDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var reading = await db.ReadingLogs.Where(l => l.Date >= from && l.Date <= to).SumAsync(l => l.Minutes, ct);
        var start = clock.DayBoundsUtc(from).StartUtc;
        var end = clock.DayBoundsUtc(to).EndUtc;
        var seconds = await db.SessionItems.Where(i => i.StudySession!.StartUtc >= start && i.StudySession.StartUtc < end).SumAsync(i => i.TimeSeconds, ct);
        return (Math.Round(reading / 60, 1), Math.Round(seconds / 3600.0, 1));
    }

    /// <summary>Semaines du plan ; à défaut, semaines ISO depuis la première activité.</summary>
    private async Task<List<(int Week, DateOnly From, DateOnly To)>> WeeksAsync(AppDbContext db, CancellationToken ct)
    {
        var plan = await db.PlanDays.Select(d => new { d.Week, d.Date }).ToListAsync(ct);
        if (plan.Count > 0)
            return plan.GroupBy(d => d.Week).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Min(d => d.Date), g.Max(d => d.Date))).ToList();

        var today = clock.Today;
        var firstSession = await db.Sessions.OrderBy(s => s.StartUtc).Select(s => (DateTime?)s.StartUtc).FirstOrDefaultAsync(ct);
        var firstReading = await db.ReadingLogs.OrderBy(l => l.Date).Select(l => (DateOnly?)l.Date).FirstOrDefaultAsync(ct);
        var first = new[] { firstSession is { } fs ? DateOnly.FromDateTime(clock.ToLocal(fs)) : today, firstReading ?? today }.Min();
        var list = new List<(int, DateOnly, DateOnly)>();
        for (var monday = Monday(first); monday <= today; monday = monday.AddDays(7))
            list.Add((ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue)), monday, monday.AddDays(6)));
        return list;
    }

    private static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public async Task<(DateOnly From, DateOnly To)> WeekRangeAsync(int week, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var days = await db.PlanDays.Where(d => d.Week == week).Select(d => d.Date).ToListAsync(ct);
        if (days.Count > 0) return (days.Min(), days.Max());
        var monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(clock.Today.Year, Math.Clamp(week, 1, 53), DayOfWeek.Monday));
        return (monday, monday.AddDays(6));
    }

    public async Task<int> CurrentWeekAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = clock.Today;
        var planDay = await db.PlanDays.Where(d => d.Date <= today).OrderByDescending(d => d.Date).FirstOrDefaultAsync(ct);
        return planDay?.Week ?? ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>Bilan hebdomadaire en Markdown, prêt à coller pour le bilan du dimanche avec Claude.</summary>
    public async Task<string> WeeklyMarkdownAsync(int week, CancellationToken ct = default)
    {
        var (from, to) = await WeekRangeAsync(week, ct);
        var stats = await StatsAsync(from, to, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var sb = new StringBuilder();

        sb.AppendLine($"# Bilan PMP — semaine {week} ({from.ToString("d MMMM", fr)} → {to.ToString("d MMMM yyyy", fr)})").AppendLine();

        var examDate = await study.ExamDateAsync(ct);
        sb.AppendLine($"- Jours avant l'examen (au {to.ToString("d MMMM", fr)}) : **{examDate.DayNumber - to.DayNumber}**");
        var weeklyTarget = double.TryParse(await study.GetSettingAsync(SettingKeys.WeeklyHoursTarget, ct), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : StudyService.DefaultWeeklyHours;
        var (readingH, questionH) = await HoursAsync(db, from, to, ct);
        sb.AppendLine($"- Heures réalisées : **{readingH + questionH:0.#} h** sur {weeklyTarget:0.#} h prévues (lecture {readingH:0.#} h, questions {questionH:0.#} h)");
        sb.AppendLine($"- Questions répondues : **{stats.Answered}**, justes : **{stats.Correct}** ({stats.Percent:0.#} %, {Scoring.Label(Scoring.LevelFor(stats.Percent))})");
        sb.AppendLine($"- Temps moyen par question : **{stats.AverageSecondsPerQuestion:0} s** (cible 80 s)").AppendLine();

        var plan = await db.PlanDays.Where(d => d.Date >= from && d.Date <= to).OrderBy(d => d.Date).ToListAsync(ct);
        var logs = await db.ReadingLogs.Where(l => l.Date >= from && l.Date <= to).ToListAsync(ct);
        var offset = await study.PageOffsetAsync(ct);
        if (plan.Count > 0)
        {
            sb.AppendLine("## Plan de la semaine").AppendLine();
            sb.AppendLine("| Jour | Pages prévues | Pages lues | Objectif questions | Statut |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in plan)
            {
                var read = StudyService.MergeRanges(logs.Where(l => l.Date == d.Date).Select(l => new PageRange(l.PageFrom - offset, l.PageTo - offset)));
                var (s, e) = clock.DayBoundsUtc(d.Date);
                var answered = await db.SessionItems.CountAsync(i => i.AnsweredUtc >= s && i.AnsweredUtc < e, ct);
                sb.AppendLine($"| {d.Date.ToString("ddd d", fr)} | {(d.PageFrom is null ? "—" : $"{d.PageFrom}–{d.PageTo}")} | {(read.Count == 0 ? "—" : string.Join(", ", read.Select(r => $"{r.From}–{r.To}")))} | {answered}/{d.QuestionGoal} | {StatusLabel(d.Status)} |");
            }
            sb.AppendLine();
        }

        AppendTable(sb, "Score par domaine", stats.ByDomain);
        AppendTable(sb, "Score par approche", stats.ByApproach);
        AppendTable(sb, "Score par tâche ECO", stats.ByEcoTask);

        var exams = stats.Exams.Where(e => DateOnly.FromDateTime(clock.ToLocal(e.StartUtc)) is var d && d >= from && d <= to).ToList();
        if (exams.Count > 0)
        {
            sb.AppendLine("## Examens blancs").AppendLine();
            foreach (var e in exams)
                sb.AppendLine($"- {e.Title} : **{e.Score:0.#} %** sur {e.Questions} questions — " +
                              string.Join(", ", e.ByDomain.Select(l => $"{l.Label} {l.Percent:0} %")));
            sb.AppendLine();
        }

        var (startUtc, _) = clock.DayBoundsUtc(from);
        var (_, endUtc) = clock.DayBoundsUtc(to);
        var errors = await db.ErrorEntries.AsNoTracking().Include(e => e.EcoTask).Include(e => e.Question)
            .Where(e => e.UpdatedUtc >= startUtc && e.UpdatedUtc < endUtc).ToListAsync(ct);
        var today = clock.Today;
        sb.AppendLine("## Carnet d'erreurs").AppendLine();
        sb.AppendLine($"- Nouvelles entrées : **{errors.Count(e => e.CreatedUtc >= startUtc && e.CreatedUtc < endUtc)}**");
        sb.AppendLine($"- Passées en « maîtrisée » ou revues : **{errors.Count(e => e.Status == ErrorStatus.Mastered)}** maîtrisées parmi les entrées touchées");
        sb.AppendLine($"- À revoir aujourd'hui : **{await db.ErrorEntries.CountAsync(e => e.Status == ErrorStatus.Active && e.NextReview <= today, ct)}**, incomplètes : **{await db.ErrorEntries.CountAsync(e => e.Why == null || e.Why == "" || e.Trap == null || e.Trap == "", ct)}**").AppendLine();

        var weakest = stats.ByEcoTask.Where(l => l.Total >= 3).OrderBy(l => l.Percent).Take(3).ToList();
        if (weakest.Count > 0)
        {
            sb.AppendLine("**Tâches ECO les plus faibles :** " + string.Join(" ; ", weakest.Select(l => $"{l.Key} ({l.Percent:0} % sur {l.Total})"))).AppendLine();
        }

        var detailed = errors.Where(e => e.Status == ErrorStatus.Active).OrderByDescending(e => e.TimesWrong).Take(15).ToList();
        if (detailed.Count > 0)
        {
            sb.AppendLine("### Erreurs actives de la semaine").AppendLine();
            foreach (var e in detailed)
            {
                var title = e.Question?.Stem ?? e.Summary ?? "(sans résumé)";
                if (title.Length > 160) title = title[..157] + "…";
                sb.AppendLine($"- **{e.EcoTask?.Code ?? "—"}** — {title.ReplaceLineEndings(" ")} (×{e.TimesWrong})");
                if (!string.IsNullOrWhiteSpace(e.Why)) sb.AppendLine($"  - Pourquoi : {e.Why.ReplaceLineEndings(" ")}");
                if (!string.IsNullOrWhiteSpace(e.Trap)) sb.AppendLine($"  - Piège : {e.Trap.ReplaceLineEndings(" ")}");
                if (!string.IsNullOrWhiteSpace(e.MindsetReflex)) sb.AppendLine($"  - Réflexe mindset : {e.MindsetReflex.ReplaceLineEndings(" ")}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Questions pour le bilan avec Claude").AppendLine();
        sb.AppendLine("1. Quelles tâches ECO prioriser la semaine prochaine ?");
        sb.AppendLine("2. Le rythme (pages, questions, temps par question) est-il tenable jusqu'à l'examen ?");
        sb.AppendLine("3. Quels réflexes mindset retravailler à partir des erreurs ci-dessus ?");
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string title, IReadOnlyList<ScoreLine> lines)
    {
        if (lines.Count == 0) return;
        sb.AppendLine($"## {title}").AppendLine();
        sb.AppendLine("| | Justes | Total | % | Niveau |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var l in lines)
            sb.AppendLine($"| {l.Label} | {l.Correct} | {l.Total} | {l.Percent:0.#} | {Scoring.Label(l.Level)} |");
        sb.AppendLine();
    }

    public static string StatusLabel(PlanDayStatus status) => status switch
    {
        PlanDayStatus.Done => "Fait",
        PlanDayStatus.InProgress => "En cours",
        PlanDayStatus.Skipped => "Sauté",
        _ => "À faire",
    };

    /// <summary>Export JSON complet (notes, erreurs, sessions, plan, réglages) pour sauvegarde.</summary>
    public async Task<string> FullExportJsonAsync(ContentService content, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var export = new
        {
            exportedUtc = clock.UtcNow,
            settings = await db.Settings.AsNoTracking().ToListAsync(ct),
            chapters = await db.Chapters.AsNoTracking().Select(c => new { c.Id, c.Code, c.Title, c.StartPage, c.EndPage, c.ParentId }).ToListAsync(ct),
            plan = await db.PlanDays.AsNoTracking().Select(d => new
            {
                d.Date, d.Week, d.PageFrom, d.PageTo, d.QuestionGoal, d.Label, d.Status,
                chapters = d.Chapters.Select(c => c.Code), ecoTasks = d.EcoTasks.Select(t => t.Code),
            }).ToListAsync(ct),
            readingLogs = await db.ReadingLogs.AsNoTracking().Select(l => new { l.Date, l.PageFrom, l.PageTo, l.Minutes }).ToListAsync(ct),
            notes = await db.Notes.AsNoTracking().Select(n => new { n.Id, n.Page, n.Content, n.Tags, n.CreatedUtc, n.UpdatedUtc, chapter = n.Chapter!.Code, ecoTask = n.EcoTask!.Code }).ToListAsync(ct),
            questions = await content.ExportQuestionsAsync(ct),
            sessions = await db.Sessions.AsNoTracking().Select(s => new
            {
                s.Id, s.Type, s.Title, s.StartUtc, s.EndUtc, s.DurationSeconds, s.ScorePercent, s.CorrectCount, s.FiltersJson,
                sections = s.Sections.Select(x => new { x.Index, x.StartUtc, x.EndUtc, x.BreakStartUtc, x.BreakEndUtc }),
                items = s.Items.OrderBy(i => i.Order).Select(i => new
                {
                    i.Order, i.SectionIndex, question = i.Question!.ExternalId ?? "db-" + i.QuestionId, i.AnswerJson, i.IsCorrect, i.Flagged, i.Doubt, i.TimeSeconds,
                }),
            }).ToListAsync(ct),
            errors = await db.ErrorEntries.AsNoTracking().Select(e => new
            {
                e.Id, question = e.Question!.ExternalId, ecoTask = e.EcoTask!.Code, e.Summary, e.Why, e.Trap, e.MindsetReflex,
                e.Status, e.Origin, e.ExternalSource, e.NextReview, e.ReviewCount, e.IntervalStep, e.ConsecutiveCorrect, e.TimesWrong, e.CreatedUtc, e.UpdatedUtc,
            }).ToListAsync(ct),
        };
        return JsonSerializer.Serialize(export, ContentService.Json);
    }
}
