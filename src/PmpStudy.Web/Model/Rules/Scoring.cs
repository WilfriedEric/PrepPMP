using System.Text.Json;
using PmpStudy.Web.Model.Entities;

namespace PmpStudy.Web.Model.Rules;

public record ScoreLine(string Key, string Label, int Correct, int Total)
{
    public double Percent => Total == 0 ? 0 : Math.Round(100.0 * Correct / Total, 1);
    public PerformanceLevel Level => Scoring.LevelFor(Percent);
}

public enum PerformanceLevel
{
    NeedsImprovement = 0,
    BelowTarget = 1,
    Target = 2,
    AboveTarget = 3,
}

public static class Scoring
{
    public static IReadOnlyList<int> ParseAnswer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<int>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeAnswer(IEnumerable<int> optionIds) =>
        JsonSerializer.Serialize(optionIds.Distinct().Order().ToList());

    /// <summary>Juste si et seulement si l'ensemble des options choisies est exactement l'ensemble des bonnes options.</summary>
    public static bool IsCorrect(Question question, IReadOnlyCollection<int> selectedOptionIds)
    {
        if (selectedOptionIds.Count == 0) return false;
        var correct = question.Options.Where(o => o.IsCorrect).Select(o => o.Id).ToHashSet();
        return correct.SetEquals(selectedOptionIds);
    }

    /// <summary>
    /// Niveaux internes (PMI ne publie pas de note de passage) : 80 % et plus au-dessus de la cible,
    /// 70 à 79 % cible, 60 à 69 % sous la cible, moins de 60 % à renforcer.
    /// </summary>
    public static PerformanceLevel LevelFor(double percent) => percent switch
    {
        >= 80 => PerformanceLevel.AboveTarget,
        >= 70 => PerformanceLevel.Target,
        >= 60 => PerformanceLevel.BelowTarget,
        _ => PerformanceLevel.NeedsImprovement,
    };

    public static string Label(PerformanceLevel level) => level switch
    {
        PerformanceLevel.AboveTarget => "Au-dessus de la cible",
        PerformanceLevel.Target => "Cible",
        PerformanceLevel.BelowTarget => "Sous la cible",
        _ => "À renforcer",
    };

    public static string Label(Domain domain) => domain switch
    {
        Domain.People => "Personnes",
        Domain.Process => "Processus",
        Domain.BusinessEnvironment => "Environnement d'affaires",
        _ => domain.ToString(),
    };

    public static string Label(Approach approach) => approach switch
    {
        Approach.Predictive => "Prédictive",
        Approach.Agile => "Agile",
        Approach.Hybrid => "Hybride",
        _ => approach.ToString(),
    };

    /// <summary>Items dont la question (avec tâche ECO) est chargée.</summary>
    public static IReadOnlyList<ScoreLine> ByDomain(IEnumerable<SessionItem> items) =>
        items.Where(i => i.Question?.EcoTask is not null)
            .GroupBy(i => i.Question!.EcoTask!.Domain)
            .OrderBy(g => g.Key)
            .Select(g => new ScoreLine(g.Key.ToString(), Label(g.Key), g.Count(i => i.IsCorrect == true), g.Count()))
            .ToList();

    public static IReadOnlyList<ScoreLine> ByEcoTask(IEnumerable<SessionItem> items) =>
        items.Where(i => i.Question?.EcoTask is not null)
            .GroupBy(i => i.Question!.EcoTask!.Code)
            .Select(g => (Task: g.First().Question!.EcoTask!, Items: g))
            .OrderBy(g => g.Task.Domain).ThenBy(g => EcoCatalog.SortKey(g.Task.Code))
            .Select(g => new ScoreLine(g.Task.Code, $"{g.Task.Code} · {g.Task.Title}", g.Items.Count(i => i.IsCorrect == true), g.Items.Count()))
            .ToList();

    public static IReadOnlyList<ScoreLine> ByApproach(IEnumerable<SessionItem> items) =>
        items.Where(i => i.Question is not null)
            .GroupBy(i => i.Question!.Approach)
            .OrderBy(g => g.Key)
            .Select(g => new ScoreLine(g.Key.ToString(), Label(g.Key), g.Count(i => i.IsCorrect == true), g.Count()))
            .ToList();
}
