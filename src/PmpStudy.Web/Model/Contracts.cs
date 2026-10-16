using System.Text.Json.Serialization;

namespace PmpStudy.Web.Model;

/// <summary>Heure du serveur, convertie dans le fuseau de l'utilisateur pour la notion de « jour ».</summary>
public interface IStudyClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
    DateTime ToLocal(DateTime utc);
    /// <summary>Bornes UTC d'un jour local.</summary>
    (DateTime StartUtc, DateTime EndUtc) DayBoundsUtc(DateOnly day);
}

/// <summary>Stockage privé du PDF du cours (disque local ou Azure Blob Storage).</summary>
public interface IPdfStore
{
    Task<bool> ExistsAsync(CancellationToken ct = default);
    Task SaveAsync(Stream content, CancellationToken ct = default);
    /// <summary>Flux positionnable, pour servir les requêtes partielles (Range).</summary>
    Task<Stream?> OpenReadAsync(CancellationToken ct = default);
    Task<long?> GetLengthAsync(CancellationToken ct = default);
}

/// <summary>Format d'import d'une question (voir le brief technique).</summary>
public class QuestionImport
{
    public string? Id { get; set; }
    public string Type { get; set; } = "single";
    public string? EcoTask { get; set; }
    public string Approach { get; set; } = "predictive";
    public string? Chapter { get; set; }
    public string? Scenario { get; set; }
    public string Stem { get; set; } = "";
    public List<OptionImport> Options { get; set; } = [];
    public string Explanation { get; set; } = "";
    public string Source { get; set; } = "";
    public int Difficulty { get; set; } = 2;
    public string Language { get; set; } = "fr";
}

public class OptionImport
{
    public string Text { get; set; } = "";
    public bool Correct { get; set; }
    public string? MatchKey { get; set; }
}

/// <summary>Une journée du plan de révision.</summary>
public class PlanDayImport
{
    public DateOnly Date { get; set; }
    public int Week { get; set; }
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public int QuestionGoal { get; set; }
    public string? Label { get; set; }
    public List<string> Chapters { get; set; } = [];
    public List<string> EcoTasks { get; set; } = [];
}

public class ChapterImport
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public int StartPage { get; set; }
    public int EndPage { get; set; }
    public string? Parent { get; set; }
}

public record ImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Errors)
{
    [JsonIgnore]
    public bool Ok => Errors.Count == 0;
}
