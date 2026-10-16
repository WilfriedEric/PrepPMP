using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Model.Rules;
using PmpStudy.Web.Model;

namespace PmpStudy.Web.Services;

public class TrainingRequest
{
    public int Count { get; set; } = 20;
    public int SecondsPerQuestion { get; set; } = SessionClock.DefaultSecondsPerQuestion;
    public FeedbackMode FeedbackMode { get; set; } = FeedbackMode.Immediate;
    public Domain? Domain { get; set; }
    public List<string> EcoTasks { get; set; } = [];
    public Approach? Approach { get; set; }
    public int? ChapterId { get; set; }
    public string? Source { get; set; }
    /// <summary>Questions jamais répondues en priorité.</summary>
    public bool PreferUnseen { get; set; } = true;
}

public record AnswerResult(bool Accepted, string? Error, bool? IsCorrect, bool SessionFinished);

public class SessionException(string message) : Exception(message);

public class SessionService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock)
{
    private static readonly JsonSerializerOptions FilterJson = new() { WriteIndented = false };

    // ---------- Création ----------

    public async Task<int> CountMatchingAsync(TrainingRequest request, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Filter(db.Questions, request).CountAsync(ct);
    }

    public async Task<int> CreateTrainingAsync(TrainingRequest request, CancellationToken ct = default)
    {
        if (request.Count < 1) throw new SessionException("Choisis au moins une question.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var candidates = await Filter(db.Questions, request)
            .Select(q => new { q.Id, Seen = db.SessionItems.Any(i => i.QuestionId == q.Id && i.AnsweredUtc != null) })
            .ToListAsync(ct);
        if (candidates.Count == 0) throw new SessionException("Aucune question ne correspond à ces filtres.");

        var ids = candidates
            .OrderBy(c => request.PreferUnseen && c.Seen ? 1 : 0)
            .ThenBy(_ => Random.Shared.Next())
            .Take(request.Count)
            .Select(c => c.Id)
            .ToList();

        var title = $"Entraînement · {ids.Count} questions";
        var session = NewSession(SessionType.Training, request.FeedbackMode, ids.Count * request.SecondsPerQuestion,
            JsonSerializer.Serialize(request, FilterJson), title);
        AddItems(session, ids, sectionSize: int.MaxValue);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    public async Task<(int SessionId, int QuestionCount)> CreateExamAsync(int questionCount = SessionClock.ExamQuestionCount, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.Questions.Where(q => q.IsActive)
            .Select(q => new ExamCandidate(
                q.Id,
                q.EcoTask != null ? q.EcoTask.Domain : null,
                q.Approach,
                db.SessionItems.Count(i => i.QuestionId == q.Id && i.StudySession!.Type == SessionType.Exam)))
            .ToListAsync(ct);
        if (candidates.Count == 0) throw new SessionException("La banque de questions est vide.");

        var ids = ExamComposer.Compose(candidates, questionCount, Random.Shared);
        // Durée au prorata si la banque ne permet pas 180 questions : 80 s par question.
        var duration = ids.Count == SessionClock.ExamQuestionCount
            ? (int)SessionClock.ExamDuration.TotalSeconds
            : ids.Count * SessionClock.DefaultSecondsPerQuestion;
        var sectionSize = (int)Math.Ceiling(ids.Count / 3.0);

        var count = await db.Sessions.CountAsync(s => s.Type == SessionType.Exam, ct);
        var session = NewSession(SessionType.Exam, FeedbackMode.AtEnd, duration,
            JsonSerializer.Serialize(new { questionCount }, FilterJson), $"Examen blanc n°{count + 1}");
        var sections = ids.Count <= sectionSize ? 1 : (int)Math.Ceiling(ids.Count / (double)sectionSize);
        for (var i = 1; i <= sections; i++)
            session.Sections.Add(new SessionSection { Index = i, StartUtc = i == 1 ? session.StartUtc : null });
        AddItems(session, ids, sectionSize);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session.Id, ids.Count);
    }

    public async Task<int> CreateErrorReplayAsync(bool dueOnly, int max = 30, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = clock.Today;
        var query = db.ErrorEntries.Where(e => e.Status == ErrorStatus.Active && e.QuestionId != null);
        if (dueOnly) query = query.Where(e => e.NextReview <= today);
        var ids = await query.OrderBy(e => e.NextReview).Take(max).Select(e => e.QuestionId!.Value).ToListAsync(ct);
        if (ids.Count == 0) throw new SessionException(dueOnly ? "Aucune erreur à revoir aujourd'hui." : "Le carnet ne contient aucune erreur active issue de l'app.");

        ids = ids.OrderBy(_ => Random.Shared.Next()).ToList();
        var session = NewSession(SessionType.ErrorReplay, FeedbackMode.Immediate, ids.Count * SessionClock.DefaultSecondsPerQuestion,
            JsonSerializer.Serialize(new { dueOnly, max }, FilterJson), $"Rejeu d'erreurs · {ids.Count} questions");
        AddItems(session, ids, sectionSize: int.MaxValue);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    private StudySession NewSession(SessionType type, FeedbackMode feedback, int durationSeconds, string filters, string title)
    {
        var now = clock.UtcNow;
        return new StudySession
        {
            Type = type,
            FeedbackMode = feedback,
            StartUtc = now,
            DurationSeconds = durationSeconds,
            DeadlineUtc = now.AddSeconds(durationSeconds),
            FiltersJson = filters,
            Title = title,
        };
    }

    private static void AddItems(StudySession session, IReadOnlyList<int> questionIds, int sectionSize)
    {
        for (var i = 0; i < questionIds.Count; i++)
            session.Items.Add(new SessionItem
            {
                QuestionId = questionIds[i],
                Order = i + 1,
                SectionIndex = sectionSize == int.MaxValue ? 1 : i / sectionSize + 1,
            });
    }

    private static IQueryable<Question> Filter(IQueryable<Question> query, TrainingRequest r)
    {
        query = query.Where(q => q.IsActive);
        if (r.Domain is { } domain) query = query.Where(q => q.EcoTask != null && q.EcoTask.Domain == domain);
        if (r.EcoTasks.Count > 0) query = query.Where(q => q.EcoTask != null && r.EcoTasks.Contains(q.EcoTask.Code));
        if (r.Approach is { } approach) query = query.Where(q => q.Approach == approach);
        if (r.ChapterId is { } chapterId) query = query.Where(q => q.ChapterId == chapterId);
        if (!string.IsNullOrWhiteSpace(r.Source)) query = query.Where(q => q.Source == r.Source);
        return query;
    }

    // ---------- Lecture ----------

    /// <summary>Charge la session ; la clôture si son échéance est passée.</summary>
    public async Task<StudySession?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await LoadAsync(db, id, ct);
        if (session is null) return null;
        if (SessionClock.IsExpired(session, clock.UtcNow))
            await FinishCoreAsync(db, session, ct);
        return session;
    }

    public async Task<List<StudySession>> ListAsync(SessionType? type = null, int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Sessions.AsNoTracking().Include(s => s.Items).AsQueryable();
        if (type is { } t) query = query.Where(s => s.Type == t);
        return await query.OrderByDescending(s => s.StartUtc).Take(take).ToListAsync(ct);
    }

    public async Task<StudySession?> GetOpenSessionAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var open = await db.Sessions.Include(s => s.Sections).Where(s => s.EndUtc == null)
            .OrderByDescending(s => s.StartUtc).ToListAsync(ct);
        return open.FirstOrDefault(s => !SessionClock.IsExpired(s, now));
    }

    private static Task<StudySession?> LoadAsync(AppDbContext db, int id, CancellationToken ct) =>
        db.Sessions
            .Include(s => s.Sections)
            .Include(s => s.Items).ThenInclude(i => i.Question).ThenInclude(q => q!.Options)
            .Include(s => s.Items).ThenInclude(i => i.Question).ThenInclude(q => q!.EcoTask)
            .Include(s => s.Items).ThenInclude(i => i.Question).ThenInclude(q => q!.Chapter)
            .Include(s => s.Items).ThenInclude(i => i.Question).ThenInclude(q => q!.Scenario)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    // ---------- Réponses ----------

    public async Task<AnswerResult> AnswerAsync(int sessionId, int itemId, IReadOnlyCollection<int> optionIds, int elapsedSeconds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.Include(s => s.Sections).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return new(false, "Session introuvable.", null, false);
        var now = clock.UtcNow;
        if (session.EndUtc is not null) return new(false, "La session est terminée.", null, true);
        if (SessionClock.IsExpired(session, now))
        {
            await FinishAsync(sessionId, ct);
            return new(false, "Temps écoulé : la session a été clôturée.", null, true);
        }

        var item = await db.SessionItems.Include(i => i.Question).ThenInclude(q => q!.Options)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.StudySessionId == sessionId, ct);
        if (item?.Question is null) return new(false, "Question introuvable.", null, false);

        var lockError = CheckWritable(session, item);
        if (lockError is not null) return new(false, lockError, null, false);
        if (session.FeedbackMode == FeedbackMode.Immediate && item.AnsweredUtc is not null)
            return new(false, "Réponse déjà corrigée.", item.IsCorrect, false);

        var validIds = item.Question.Options.Select(o => o.Id).ToHashSet();
        var selected = optionIds.Where(validIds.Contains).Distinct().ToList();
        item.AnswerJson = Scoring.SerializeAnswer(selected);
        item.IsCorrect = Scoring.IsCorrect(item.Question, selected);
        item.AnsweredUtc = selected.Count > 0 ? now : null;
        item.TimeSeconds += Math.Clamp(elapsedSeconds, 0, 3600);
        await db.SaveChangesAsync(ct);

        return new(true, null, session.FeedbackMode == FeedbackMode.Immediate ? item.IsCorrect : null, false);
    }

    /// <summary>Ajoute le temps passé sur une question sans changer la réponse (navigation).</summary>
    public async Task AddTimeAsync(int sessionId, int itemId, int elapsedSeconds, CancellationToken ct = default)
    {
        if (elapsedSeconds <= 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SessionItems.Where(i => i.Id == itemId && i.StudySessionId == sessionId && i.StudySession!.EndUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.TimeSeconds, i => i.TimeSeconds + Math.Min(elapsedSeconds, 3600)), ct);
    }

    public async Task<string?> SetMarksAsync(int sessionId, int itemId, bool flagged, bool doubt, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.Include(s => s.Sections).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        var item = await db.SessionItems.FirstOrDefaultAsync(i => i.Id == itemId && i.StudySessionId == sessionId, ct);
        if (session is null || item is null) return "Question introuvable.";
        if (session.EndUtc is not null) return "La session est terminée.";
        var lockError = CheckWritable(session, item);
        if (lockError is not null) return lockError;
        item.Flagged = flagged;
        item.Doubt = doubt;
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static string? CheckWritable(StudySession session, SessionItem item)
    {
        if (session.Type != SessionType.Exam) return null;
        if (SessionClock.ActiveBreak(session) is not null) return "Pause en cours.";
        var current = SessionClock.CurrentSection(session);
        if (current is null || current.StartUtc is null) return "Aucune section ouverte.";
        if (item.SectionIndex != current.Index) return "Cette section est verrouillée.";
        return null;
    }

    // ---------- Sections et pauses (examen) ----------

    /// <summary>Valide (verrouille) la section courante. Clôture l'examen après la dernière.</summary>
    public async Task<string?> SubmitSectionAsync(int sessionId, int index, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.Include(s => s.Sections).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.Type != SessionType.Exam) return "Examen introuvable.";
        if (session.EndUtc is not null) return "L'examen est terminé.";
        var current = SessionClock.CurrentSection(session);
        if (current is null || current.Index != index || current.StartUtc is null) return "Section non ouverte.";
        current.EndUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        if (session.Sections.All(s => s.EndUtc is not null))
            await FinishAsync(sessionId, ct);
        return null;
    }

    /// <summary>Pause de 10 min possible après une section validée, avant d'ouvrir la suivante.</summary>
    public async Task<string?> StartBreakAsync(int sessionId, int afterSection, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.Include(s => s.Sections).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.EndUtc is not null) return "Examen introuvable ou terminé.";
        var section = session.Sections.FirstOrDefault(s => s.Index == afterSection);
        var next = session.Sections.FirstOrDefault(s => s.Index == afterSection + 1);
        if (section?.EndUtc is null || next is null) return "Pause impossible ici.";
        if (next.StartUtc is not null) return "La section suivante a déjà commencé.";
        if (section.BreakStartUtc is not null) return "Pause déjà prise.";
        section.BreakStartUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Termine la pause éventuelle et ouvre la section suivante.</summary>
    public async Task<string?> StartNextSectionAsync(int sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.Include(s => s.Sections).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.EndUtc is not null) return "Examen introuvable ou terminé.";
        var now = clock.UtcNow;
        var onBreak = SessionClock.ActiveBreak(session);
        if (onBreak is not null) onBreak.BreakEndUtc = now;
        var next = SessionClock.CurrentSection(session);
        if (next is null) return "Plus de section à ouvrir.";
        next.StartUtc ??= now;
        await db.SaveChangesAsync(ct);
        return null;
    }

    // ---------- Clôture ----------

    public async Task<StudySession?> FinishAsync(int sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await LoadAsync(db, sessionId, ct);
        if (session is null) return null;
        if (session.EndUtc is null) await FinishCoreAsync(db, session, ct);
        return session;
    }

    /// <summary>Clôture les sessions dont l'échéance est passée (service d'arrière-plan).</summary>
    public async Task<int> CloseExpiredAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var open = await db.Sessions.Include(s => s.Sections).Where(s => s.EndUtc == null).ToListAsync(ct);
        var expired = open.Where(s => SessionClock.IsExpired(s, now)).Select(s => s.Id).ToList();
        foreach (var id in expired) await FinishAsync(id, ct);
        return expired.Count;
    }

    private async Task FinishCoreAsync(AppDbContext db, StudySession session, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var deadline = SessionClock.EffectiveDeadline(session, now);
        session.EndUtc = now < deadline ? now : deadline;
        foreach (var section in session.Sections)
        {
            section.StartUtc ??= session.EndUtc;
            section.EndUtc ??= session.EndUtc;
            if (section.BreakStartUtc is not null) section.BreakEndUtc ??= session.EndUtc;
        }

        // Les questions sans réponse comptent comme fausses.
        foreach (var item in session.Items)
        {
            var selected = Scoring.ParseAnswer(item.AnswerJson);
            item.IsCorrect = item.Question is not null && Scoring.IsCorrect(item.Question, selected.ToList());
        }

        session.CorrectCount = session.Items.Count(i => i.IsCorrect == true);
        session.ScorePercent = session.Items.Count == 0 ? 0 : Math.Round(100.0 * session.CorrectCount.Value / session.Items.Count, 1);

        await ErrorLogService.ApplySessionAsync(db, session, clock.Today, now, ct);
        await db.SaveChangesAsync(ct);
    }
}
