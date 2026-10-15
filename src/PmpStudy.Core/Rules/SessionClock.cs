using PmpStudy.Core.Entities;

namespace PmpStudy.Core.Rules;

/// <summary>
/// Le serveur fait foi pour le temps. L'échéance de base est fixée à l'ouverture
/// (<c>Échéance = Début + durée</c>) ; chaque pause d'examen la repousse de sa durée,
/// plafonnée à <see cref="BreakAllowance"/> : un dépassement est donc déduit du temps restant.
/// </summary>
public static class SessionClock
{
    public static readonly TimeSpan BreakAllowance = TimeSpan.FromMinutes(10);
    public const int ExamQuestionCount = 180;
    public const int ExamSectionSize = 60;
    public static readonly TimeSpan ExamDuration = TimeSpan.FromMinutes(240);
    /// <summary>240 min / 180 questions.</summary>
    public const int DefaultSecondsPerQuestion = 80;

    public static TimeSpan BreakCredit(SessionSection section, DateTime nowUtc)
    {
        if (section.BreakStartUtc is not { } start) return TimeSpan.Zero;
        var end = section.BreakEndUtc ?? nowUtc;
        var taken = end - start;
        if (taken < TimeSpan.Zero) taken = TimeSpan.Zero;
        return taken < BreakAllowance ? taken : BreakAllowance;
    }

    public static DateTime EffectiveDeadline(StudySession session, DateTime nowUtc)
    {
        var deadline = session.DeadlineUtc;
        foreach (var section in session.Sections)
            deadline += BreakCredit(section, nowUtc);
        return deadline;
    }

    public static TimeSpan Remaining(StudySession session, DateTime nowUtc)
    {
        if (session.EndUtc is not null) return TimeSpan.Zero;
        var remaining = EffectiveDeadline(session, nowUtc) - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static bool IsExpired(StudySession session, DateTime nowUtc) =>
        session.EndUtc is null && EffectiveDeadline(session, nowUtc) <= nowUtc;

    public static SessionSection? ActiveBreak(StudySession session) =>
        session.Sections.FirstOrDefault(s => s.BreakStartUtc is not null && s.BreakEndUtc is null);

    /// <summary>Temps de pause restant (négatif en cas de dépassement).</summary>
    public static TimeSpan BreakRemaining(SessionSection section, DateTime nowUtc) =>
        section.BreakStartUtc is { } start ? start + BreakAllowance - nowUtc : TimeSpan.Zero;

    /// <summary>Première section non validée, ou null si toutes le sont.</summary>
    public static SessionSection? CurrentSection(StudySession session) =>
        session.Sections.OrderBy(s => s.Index).FirstOrDefault(s => s.EndUtc is null);
}
