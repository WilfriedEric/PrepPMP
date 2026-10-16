using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Model.Rules;

namespace PmpStudy.Web.Services;

/// <summary>Réglages du plan automatique, stockés dans la table Settings.</summary>
public class PlanConfig
{
    public bool Auto { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public PlanUnit Unit { get; set; }
    public int FromPage { get; set; } = 1;
    public int ToPage { get; set; }
    public List<DayOfWeek> RestDays { get; set; } = [DayOfWeek.Sunday];
    public bool SkipSimulations { get; set; } = true;
    public int QuestionGoal { get; set; } = 30;

    public PlanSettings ToSettings() => new(Start, End, Unit, FromPage, ToPage, RestDays, SkipSimulations, QuestionGoal);
}

/// <summary>
/// Plan de révision automatique : configuration depuis l'interface, aperçu, génération, et recalcul quotidien
/// selon la progression réelle (la reprise se fait à la première page du programme pas encore lue).
/// </summary>
public class PlanService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static class Keys
    {
        public const string Mode = "PlanMode";
        public const string Start = "PlanStart";
        public const string End = "PlanEnd";
        public const string Unit = "PlanUnit";
        public const string FromPage = "PlanFromPage";
        public const string ToPage = "PlanToPage";
        public const string RestDays = "PlanRestDays";
        public const string SkipSimulations = "PlanSkipSimulations";
        public const string QuestionGoal = "PlanQuestionGoal";
        public const string LastGenerated = "PlanLastGenerated";
    }

    // ---------- Configuration ----------

    public async Task<PlanConfig> GetConfigAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Settings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var context = await LoadContextAsync(db, s, ct);
        var today = clock.Today;
        var defaultEnd = context.ExamDate.AddDays(-14) > today.AddDays(7) ? context.ExamDate.AddDays(-14) : context.ExamDate.AddDays(-1);
        return new PlanConfig
        {
            Auto = s.GetValueOrDefault(Keys.Mode) == "auto",
            Start = Date(s, Keys.Start) ?? today,
            End = Date(s, Keys.End) ?? defaultEnd,
            Unit = Enum.TryParse<PlanUnit>(s.GetValueOrDefault(Keys.Unit), out var unit) ? unit : context.Sections.Count > 0 ? PlanUnit.Sections : PlanUnit.Pages,
            FromPage = Int(s, Keys.FromPage) ?? 1,
            ToPage = Int(s, Keys.ToPage) ?? context.LastPage,
            RestDays = s.TryGetValue(Keys.RestDays, out var rest)
                ? rest.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? (DayOfWeek?)v : null).OfType<DayOfWeek>().ToList()
                : [DayOfWeek.Sunday],
            SkipSimulations = s.GetValueOrDefault(Keys.SkipSimulations) != "false",
            QuestionGoal = Int(s, Keys.QuestionGoal) ?? 30,
        };
    }

    // ---------- Aperçu et génération ----------

    /// <summary>Calcule le plan sans rien enregistrer (aperçu en direct dans l'interface).</summary>
    public async Task<PlanSummary> PreviewAsync(PlanConfig config, int? resumePage = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Settings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var context = await LoadContextAsync(db, s, ct);
        var resume = resumePage ?? await FirstUnreadPageAsync(db, config, context.Offset, ct);
        return PlanGenerator.Generate(config.ToSettings(), clock.Today, context.ExamDate, context.Simulations, context.Sections, resume);
    }

    /// <summary>Première page du programme pas encore lue (d'après les séances de lecture enregistrées).</summary>
    public async Task<int> ResumePageAsync(PlanConfig config, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var offset = int.TryParse((await db.Settings.FindAsync([SettingKeys.PageOffset], ct))?.Value, out var o) ? o : 0;
        return await FirstUnreadPageAsync(db, config, offset, ct);
    }

    /// <summary>Enregistre la configuration et (re)génère le plan à partir d'aujourd'hui. Les jours passés ne bougent pas.</summary>
    public async Task<PlanSummary> SaveAndGenerateAsync(PlanConfig config, int? resumePage = null, CancellationToken ct = default)
    {
        config.Auto = true;
        await SaveConfigAsync(config, ct);
        return await GenerateAsync(config, resumePage, ct);
    }

    /// <summary>Passe en plan manuel : le plan n'est plus recalculé (import JSON possible).</summary>
    public async Task DisableAutoAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Upsert(db, Keys.Mode, "manual", ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recalcul quotidien : au premier affichage de la journée, le plan suit la progression réelle.</summary>
    public async Task RefreshIfStaleAsync(CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var mode = await db.Settings.AsNoTracking().Where(x => x.Key == Keys.Mode || x.Key == Keys.LastGenerated).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
            if (mode.GetValueOrDefault(Keys.Mode) != "auto" || mode.GetValueOrDefault(Keys.LastGenerated) == clock.Today.ToString("yyyy-MM-dd")) return;
        }
        await GenerateAsync(await GetConfigAsync(ct), null, ct);
    }

    private async Task<PlanSummary> GenerateAsync(PlanConfig config, int? resumePage, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.Settings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
            var context = await LoadContextAsync(db, s, ct);
            var today = clock.Today;

            // Aujourd'hui déjà entamé ou terminé : on le garde tel quel et on replanifie à partir de demain.
            var todayDay = await db.PlanDays.AsNoTracking().FirstOrDefaultAsync(d => d.Date == today, ct);
            var keepToday = todayDay is not null && todayDay.Status != PlanDayStatus.Todo;
            var settings = config.ToSettings();
            var resume = resumePage ?? await FirstUnreadPageAsync(db, config, context.Offset, ct);
            var summary = PlanGenerator.Generate(settings, keepToday ? today.AddDays(1) : today, context.ExamDate, context.Simulations, context.Sections, resume);

            var from = keepToday ? today.AddDays(1) : today;
            var old = await db.PlanDays.Include(d => d.Chapters).Include(d => d.EcoTasks).Where(d => d.Date >= from).ToListAsync(ct);
            foreach (var d in old) { d.Chapters.Clear(); d.EcoTasks.Clear(); }
            db.PlanDays.RemoveRange(old);
            await db.SaveChangesAsync(ct);

            var chapters = await db.Chapters.ToDictionaryAsync(c => c.Id, ct);
            foreach (var draft in summary.Days)
            {
                db.PlanDays.Add(new StudyPlanDay
                {
                    Date = draft.Date,
                    Week = draft.Week,
                    PageFrom = draft.PageFrom,
                    PageTo = draft.PageTo,
                    QuestionGoal = draft.QuestionGoal,
                    Label = draft.Label.Length > 300 ? draft.Label[..300] : draft.Label,
                    Status = PlanDayStatus.Todo,
                    Chapters = draft.Sections.Select(x => chapters.GetValueOrDefault(x.ChapterId)).OfType<Chapter>().ToList(),
                });
            }
            await Upsert(db, Keys.LastGenerated, today.ToString("yyyy-MM-dd"), ct);
            await db.SaveChangesAsync(ct);
            return summary;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---------- Aides ----------

    private async Task SaveConfigAsync(PlanConfig c, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await Upsert(db, Keys.Mode, c.Auto ? "auto" : "manual", ct);
        await Upsert(db, Keys.Start, c.Start.ToString("yyyy-MM-dd"), ct);
        await Upsert(db, Keys.End, c.End.ToString("yyyy-MM-dd"), ct);
        await Upsert(db, Keys.Unit, c.Unit.ToString(), ct);
        await Upsert(db, Keys.FromPage, c.FromPage.ToString(CultureInfo.InvariantCulture), ct);
        await Upsert(db, Keys.ToPage, c.ToPage.ToString(CultureInfo.InvariantCulture), ct);
        await Upsert(db, Keys.RestDays, string.Join(",", c.RestDays.Distinct().Select(d => (int)d)), ct);
        await Upsert(db, Keys.SkipSimulations, c.SkipSimulations ? "true" : "false", ct);
        await Upsert(db, Keys.QuestionGoal, c.QuestionGoal.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task Upsert(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }

    private sealed record PlanContext(DateOnly ExamDate, List<DateOnly> Simulations, int Offset, List<PlanSection> Sections, int LastPage);

    private async Task<PlanContext> LoadContextAsync(AppDbContext db, Dictionary<string, string> s, CancellationToken ct)
    {
        var exam = Date(s, SettingKeys.ExamDate) ?? StudyService.DefaultExamDate;
        var sims = (s.GetValueOrDefault(SettingKeys.SimulationDates) ?? StudyService.DefaultSimulations)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => DateOnly.TryParse(x.Trim(), CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null)
            .OfType<DateOnly>().ToList();
        var offset = Int(s, SettingKeys.PageOffset) ?? 0;

        // Sections = chapitres sans sous-chapitre, convertis en pages du plan (pages imprimées).
        var all = await db.Chapters.AsNoTracking().ToListAsync(ct);
        var parents = all.Where(c => c.ParentId != null).Select(c => c.ParentId!.Value).ToHashSet();
        var sections = all.Where(c => !parents.Contains(c.Id))
            .Select(c => new PlanSection(c.Id, c.Code, c.Title, Math.Max(1, c.StartPage - offset), Math.Max(1, c.EndPage - offset)))
            .OrderBy(x => x.From).ToList();

        var pdfPages = Int(s, SettingKeys.PdfPageCount);
        var lastPage = pdfPages is { } p ? Math.Max(1, p - offset) : sections.Count > 0 ? sections.Max(x => x.To) : 500;
        return new PlanContext(exam, sims, offset, sections, lastPage);
    }

    private static async Task<int> FirstUnreadPageAsync(AppDbContext db, PlanConfig config, int offset, CancellationToken ct)
    {
        var logs = await db.ReadingLogs.AsNoTracking().Select(l => new PageRange(l.PageFrom - offset, l.PageTo - offset)).ToListAsync(ct);
        var page = Math.Max(1, config.FromPage);
        foreach (var r in StudyService.MergeRanges(logs))
        {
            if (r.To < page) continue;
            if (r.From > page) break;
            page = r.To + 1;
        }
        return page;
    }

    private static DateOnly? Date(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && DateOnly.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Int(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
