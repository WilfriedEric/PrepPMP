namespace PmpStudy.Web.Model.Rules;

public enum PlanUnit
{
    Pages = 0,
    Sections = 1,
}

/// <summary>Section du cours (chapitre sans sous-chapitre), en pages du plan (pages imprimées).</summary>
public record PlanSection(int ChapterId, string Code, string Title, int From, int To)
{
    public int Pages => Math.Max(1, To - From + 1);
}

public record PlanSettings(
    DateOnly Start,
    DateOnly End,
    PlanUnit Unit,
    int FromPage,
    int ToPage,
    IReadOnlyCollection<DayOfWeek> RestDays,
    bool SkipSimulations,
    int QuestionGoal);

public enum PlanDayKind
{
    Reading = 0,
    Review = 1,
    Simulation = 2,
}

public record PlanDraft(DateOnly Date, int Week, PlanDayKind Kind, int? PageFrom, int? PageTo, IReadOnlyList<PlanSection> Sections, string Label, int QuestionGoal);

public record PlanSummary(
    DateOnly FirstDay,
    int ReadingDays,
    int ResumePage,
    int RemainingPages,
    int RemainingSections,
    double PagesPerDay,
    double SectionsPerDay,
    DateOnly? ReadingEnds,
    IReadOnlyList<PlanDraft> Days,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Plan de révision automatique : ce qui reste à lire (à partir de la première page non lue) est réparti
/// sur les jours de lecture restants jusqu'à la date de fin, en pages ou en sections entières (équilibrées par
/// nombre de pages). Les jours suivants, jusqu'à l'examen, sont des jours de révision ; les simulations ont leur jour.
/// </summary>
public static class PlanGenerator
{
    public const int SimulationQuestions = 180;
    public const string ReviewLabel = "Révision : questions et carnet d'erreurs";
    public const string SimulationLabel = "Examen blanc";

    public static PlanSummary Generate(
        PlanSettings s, DateOnly today, DateOnly examDate, IReadOnlyCollection<DateOnly> simulations,
        IReadOnlyList<PlanSection> sections, int resumePage)
    {
        var warnings = new List<string>();
        var first = s.Start > today ? s.Start : today;
        var end = s.End >= examDate ? examDate.AddDays(-1) : s.End;
        var from = Math.Max(1, s.FromPage);
        var to = Math.Max(from, s.ToPage);
        var resume = Math.Clamp(resumePage, from, to + 1);
        var sims = simulations.ToHashSet();

        var readingDays = new List<DateOnly>();
        for (var d = first; d <= end; d = d.AddDays(1))
            if (!s.RestDays.Contains(d.DayOfWeek) && !(s.SkipSimulations && sims.Contains(d)))
                readingDays.Add(d);

        var ordered = sections.Where(x => x.To >= from && x.From <= to).OrderBy(x => x.From).ThenBy(x => x.Code, StringComparer.Ordinal).ToList();
        var remainingSections = ordered.Where(x => x.To >= resume).ToList();
        var remainingPages = Math.Max(0, to - resume + 1);

        var assignments = new Dictionary<DateOnly, Assignment>();
        if (readingDays.Count == 0 && remainingPages > 0)
            warnings.Add("Aucun jour de lecture entre la date de début et la date de fin : avance la date de fin ou réduis les jours de repos.");
        else if (s.Unit == PlanUnit.Sections && remainingSections.Count > 0)
            AssignSections(readingDays, remainingSections, resume, to, assignments);
        else if (remainingPages > 0)
            AssignPages(readingDays, resume, remainingPages, ordered, assignments);
        if (s.Unit == PlanUnit.Sections && ordered.Count == 0)
            warnings.Add("Aucune section dans cette plage de pages : importe les chapitres (signets du PDF) ou choisis le découpage par pages.");

        var days = new List<PlanDraft>();
        for (var d = first; d < examDate; d = d.AddDays(1))
        {
            var week = (d.DayNumber - s.Start.DayNumber) / 7 + 1;
            if (sims.Contains(d))
            {
                days.Add(new PlanDraft(d, week, PlanDayKind.Simulation, null, null, [], SimulationLabel, SimulationQuestions));
                continue;
            }
            if (s.RestDays.Contains(d.DayOfWeek)) continue;
            if (assignments.TryGetValue(d, out var a))
                days.Add(new PlanDraft(d, week, PlanDayKind.Reading, a.From, a.To, a.Sections,
                    a.Label ?? Label(a.From, a.To, a.Sections, ordered), s.QuestionGoal));
            else
                days.Add(new PlanDraft(d, week, PlanDayKind.Review, null, null, [], ReviewLabel, s.QuestionGoal));
        }

        var pagesPerDay = readingDays.Count == 0 ? 0 : Math.Round((double)remainingPages / readingDays.Count, 1);
        var sectionsPerDay = readingDays.Count == 0 ? 0 : Math.Round((double)remainingSections.Count / readingDays.Count, 1);
        if (pagesPerDay > 40) warnings.Add($"Environ {pagesPerDay:0} pages par jour : c'est beaucoup. Recule la date de fin ou réduis les jours de repos.");

        return new PlanSummary(first, readingDays.Count, resume, remainingPages, remainingSections.Count, pagesPerDay, sectionsPerDay,
            assignments.Count == 0 ? null : assignments.Keys.Max(), days, warnings);
    }

    /// <summary>Pages réparties au plus juste : chaque jour reçoit la même part, à une page près.</summary>
    private static void AssignPages(List<DateOnly> days, int resume, int remaining, List<PlanSection> sections,
        Dictionary<DateOnly, Assignment> assignments)
    {
        for (var i = 0; i < days.Count; i++)
        {
            var a = resume + (int)((long)i * remaining / days.Count);
            var b = resume + (int)((long)(i + 1) * remaining / days.Count) - 1;
            if (b < a) continue;
            assignments[days[i]] = new(a, b, sections.Where(x => x.From <= b && x.To >= a).ToList(), null);
        }
    }

    /// <summary>
    /// Sections dans l'ordre, équilibrées par nombre de pages. Une section plus longue qu'une journée de lecture
    /// est découpée en parties (« 3 Gouvernance (2/3) ») ; les jours de lecture se suivent sans trou.
    /// </summary>
    private static void AssignSections(List<DateOnly> days, List<PlanSection> remaining, int resume, int to,
        Dictionary<DateOnly, Assignment> assignments)
    {
        var pieces = new List<(PlanSection Section, int From, int To, string? Part)>();
        foreach (var x in remaining)
        {
            var a = Math.Max(x.From, resume);
            var b = Math.Min(x.To, to);
            if (b < a) b = a;
            pieces.Add((x, a, b, null));
        }
        var total = pieces.Sum(p => p.To - p.From + 1);
        var budget = Math.Max(1.0, (double)total / days.Count);

        var chunks = new List<(PlanSection Section, int From, int To, string? Part)>();
        foreach (var p in pieces)
        {
            var length = p.To - p.From + 1;
            var parts = Math.Max(1, (int)Math.Round(length / budget));
            for (var k = 0; k < parts; k++)
            {
                var a = p.From + (int)((long)k * length / parts);
                var b = p.From + (int)((long)(k + 1) * length / parts) - 1;
                chunks.Add((p.Section, a, b, parts > 1 ? $"{k + 1}/{parts}" : null));
            }
        }

        double done = 0;
        var index = 0;
        for (var i = 0; i < days.Count && index < chunks.Count; i++)
        {
            var target = (double)total * (i + 1) / days.Count;
            var picked = new List<(PlanSection Section, int From, int To, string? Part)>();
            var last = i == days.Count - 1;
            while (index < chunks.Count)
            {
                var w = chunks[index].To - chunks[index].From + 1;
                // Toujours au moins un morceau par jour : pas de jour vide au milieu de la lecture.
                if (!last && picked.Count > 0 && done + w / 2.0 > target) break;
                done += w;
                picked.Add(chunks[index++]);
            }
            var sections = picked.Select(c => c.Section).Distinct().ToList();
            string Name((PlanSection Section, int From, int To, string? Part) c) =>
                $"{c.Section.Code} {c.Section.Title}{(c.Part is null ? "" : $" ({c.Part})")}";
            var label = picked.Count switch
            {
                1 => Name(picked[0]),
                2 => $"{Name(picked[0])}, puis {Name(picked[1])}",
                _ => $"{Name(picked[0])} (+{picked.Count - 1} sections)",
            };
            assignments[days[i]] = new(picked[0].From, picked[^1].To, sections, label);
        }
    }

    private sealed record Assignment(int From, int To, List<PlanSection> Sections, string? Label);

    private static string Label(int from, int to, IReadOnlyList<PlanSection> picked, List<PlanSection> all)
    {
        if (picked.Count == 0)
        {
            var current = all.LastOrDefault(x => x.From <= from);
            return current is null ? $"Pages {from} à {to}" : $"{current.Code} {current.Title}";
        }
        var head = $"{picked[0].Code} {picked[0].Title}";
        return picked.Count == 1 ? head : $"{head} (+{picked.Count - 1} section{(picked.Count > 2 ? "s" : "")})";
    }
}
