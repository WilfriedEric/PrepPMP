namespace PmpStudy.Web.Model.Entities;

public class Chapter
{
    public int Id { get; set; }
    /// <summary>Numérotation du chapitre, par exemple « 2.3 ».</summary>
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public int StartPage { get; set; }
    public int EndPage { get; set; }
    public int? ParentId { get; set; }
    public Chapter? Parent { get; set; }
    public List<Chapter> Children { get; set; } = [];
    public int SortOrder { get; set; }
}

public class EcoTask
{
    public int Id { get; set; }
    /// <summary>Code de la tâche, par exemple « PR-6 ».</summary>
    public string Code { get; set; } = "";
    public Domain Domain { get; set; }
    public string Title { get; set; } = "";
}

public class StudyPlanDay
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int Week { get; set; }
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public int QuestionGoal { get; set; }
    public string? Label { get; set; }
    public PlanDayStatus Status { get; set; }
    public List<Chapter> Chapters { get; set; } = [];
    public List<EcoTask> EcoTasks { get; set; } = [];
}

public class ReadingLog
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int PageFrom { get; set; }
    public int PageTo { get; set; }
    public double Minutes { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int? StudyPlanDayId { get; set; }
    public StudyPlanDay? StudyPlanDay { get; set; }
}

public class Note
{
    public int Id { get; set; }
    public int? Page { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Étiquettes libres séparées par des virgules.</summary>
    public string Tags { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public int? EcoTaskId { get; set; }
    public EcoTask? EcoTask { get; set; }
}

public class Scenario
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public List<Question> Questions { get; set; } = [];
}

public class Question
{
    public int Id { get; set; }
    /// <summary>Identifiant stable venant du fichier d'import ; évite les doublons quand on réimporte.</summary>
    public string? ExternalId { get; set; }
    public QuestionType Type { get; set; }
    public string Stem { get; set; } = "";
    public Approach Approach { get; set; }
    public string Explanation { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>1 (facile) à 3 (difficile).</summary>
    public int Difficulty { get; set; } = 2;
    public string Language { get; set; } = "fr";
    public bool IsActive { get; set; } = true;
    public int? EcoTaskId { get; set; }
    public EcoTask? EcoTask { get; set; }
    public int? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public int? ScenarioId { get; set; }
    public Scenario? Scenario { get; set; }
    public List<QuestionOption> Options { get; set; } = [];

    public int CorrectCount => Options.Count(o => o.IsCorrect);
}

public class QuestionOption
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public Question? Question { get; set; }
    public int Order { get; set; }
    public string Text { get; set; } = "";
    public bool IsCorrect { get; set; }
    /// <summary>Réservé aux questions d'appariement (lot 3).</summary>
    public string? MatchKey { get; set; }
}

public class StudySession
{
    public int Id { get; set; }
    public SessionType Type { get; set; }
    public FeedbackMode FeedbackMode { get; set; } = FeedbackMode.AtEnd;
    public DateTime StartUtc { get; set; }
    /// <summary>Échéance fixée par le serveur (hors pauses d'examen, voir <see cref="Rules.SessionClock"/>).</summary>
    public DateTime DeadlineUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public int DurationSeconds { get; set; }
    public string FiltersJson { get; set; } = "{}";
    public int? CorrectCount { get; set; }
    public double? ScorePercent { get; set; }
    public string? Title { get; set; }
    public List<SessionSection> Sections { get; set; } = [];
    public List<SessionItem> Items { get; set; } = [];

    public bool IsFinished => EndUtc is not null;
}

public class SessionSection
{
    public int Id { get; set; }
    public int StudySessionId { get; set; }
    public StudySession? StudySession { get; set; }
    /// <summary>1 à 3.</summary>
    public int Index { get; set; }
    public DateTime? StartUtc { get; set; }
    /// <summary>Renseigné quand la section est validée : elle est alors verrouillée.</summary>
    public DateTime? EndUtc { get; set; }
    /// <summary>Pause prise après cette section.</summary>
    public DateTime? BreakStartUtc { get; set; }
    public DateTime? BreakEndUtc { get; set; }

    public bool IsLocked => EndUtc is not null;
}

public class SessionItem
{
    public int Id { get; set; }
    public int StudySessionId { get; set; }
    public StudySession? StudySession { get; set; }
    public int QuestionId { get; set; }
    public Question? Question { get; set; }
    public int Order { get; set; }
    /// <summary>Section d'examen (1 à 3) ; 1 pour les entraînements.</summary>
    public int SectionIndex { get; set; } = 1;
    /// <summary>Identifiants des options choisies, en JSON (par exemple « [12,14] »).</summary>
    public string? AnswerJson { get; set; }
    public DateTime? AnsweredUtc { get; set; }
    public bool? IsCorrect { get; set; }
    /// <summary>Marquée « à revoir ».</summary>
    public bool Flagged { get; set; }
    public bool Doubt { get; set; }
    public int TimeSeconds { get; set; }
}

public class ErrorEntry
{
    public int Id { get; set; }
    public int? QuestionId { get; set; }
    public Question? Question { get; set; }
    public int? EcoTaskId { get; set; }
    public EcoTask? EcoTask { get; set; }
    /// <summary>Résumé de la question, obligatoire pour les erreurs externes (PMI Study Hall…).</summary>
    public string? Summary { get; set; }
    public string? Why { get; set; }
    public string? Trap { get; set; }
    public string? MindsetReflex { get; set; }
    public ErrorStatus Status { get; set; }
    public ErrorOrigin Origin { get; set; }
    public string? ExternalSource { get; set; }
    public DateOnly NextReview { get; set; }
    public int ReviewCount { get; set; }
    /// <summary>Étape dans la suite J+1, J+3, J+7, J+14.</summary>
    public int IntervalStep { get; set; }
    public int ConsecutiveCorrect { get; set; }
    public int TimesWrong { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>« Incomplète » tant que « pourquoi » et « piège » ne sont pas remplis.</summary>
    public bool IsIncomplete => string.IsNullOrWhiteSpace(Why) || string.IsNullOrWhiteSpace(Trap);
}

/// <summary>Réglages et état persistant simples (dernière page lue, PDF chargé, objectifs…).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Appareil (navigateur ou PWA installée) abonné aux notifications de rappel.</summary>
public class PushDevice
{
    public int Id { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string? UserAgent { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
}

/// <summary>Fichier stocké en base (PDF du cours), pour ne rien écrire sur le disque de l'hébergement.</summary>
public class CourseFile
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public string ContentType { get; set; } = "application/pdf";
    public long Length { get; set; }
    public byte[] Content { get; set; } = [];
    public DateTime UploadedUtc { get; set; }
}
