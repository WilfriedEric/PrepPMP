using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Model.Rules;

namespace PmpStudy.Web.Services;

// ---------- Paquet téléchargé par le mode hors ligne ----------

public record OfflinePack(
    int Version,
    DateTime GeneratedUtc,
    DateOnly Today,
    string TimeZone,
    DateOnly ExamDate,
    List<DateOnly> SimulationDates,
    int PageOffset,
    int? LastPage,
    OfflinePdfInfo? Pdf,
    List<OfflineEcoTask> EcoTasks,
    List<OfflineChapter> Chapters,
    List<OfflinePlanDay> Plan,
    List<OfflineQuestion> Questions,
    List<OfflineError> Errors,
    List<OfflineNote> Notes,
    List<int> SeenQuestionIds);

public record OfflinePdfInfo(long Length, int? PageCount, string? UploadedUtc);
public record OfflineEcoTask(string Code, string Domain, string Title);
public record OfflineChapter(int Id, string Code, string Title, int StartPage, int EndPage);
public record OfflinePlanDay(DateOnly Date, int Week, int? PageFrom, int? PageTo, int QuestionGoal, string? Label, string Status, List<string> Chapters, List<string> EcoTasks);
public record OfflineOption(int Order, string Text, bool Correct);
public record OfflineQuestion(int Id, string Type, string Stem, string? Scenario, string Approach, string? EcoTask, string? Domain, int? ChapterId, string Source, string Explanation, List<OfflineOption> Options);
public record OfflineError(
    int Id, int? QuestionId, string? EcoTask, string? Summary, string? Why, string? Trap, string? MindsetReflex,
    string Status, string Origin, string? ExternalSource, DateOnly NextReview, int IntervalStep, int ConsecutiveCorrect,
    int ReviewCount, int TimesWrong, DateTime UpdatedUtc);
public record OfflineNote(int Id, string? ClientId, int? Page, string Content, string Tags, int? ChapterId, string? EcoTask, DateTime CreatedUtc, DateTime UpdatedUtc);

// ---------- Changements envoyés par l'appareil ----------

public class SyncPush
{
    public string DeviceId { get; set; } = "";
    public string? DeviceName { get; set; }
    public List<SyncSession> Sessions { get; set; } = [];
    public List<SyncNote> Notes { get; set; } = [];
    public List<SyncErrorEdit> ErrorEdits { get; set; } = [];
    public List<SyncErrorReview> ErrorReviews { get; set; } = [];
    public List<SyncReading> Readings { get; set; } = [];
    public int? LastPage { get; set; }
}

public class SyncSession
{
    public string ClientId { get; set; } = "";
    /// <summary>« training » ou « replay ».</summary>
    public string Type { get; set; } = "training";
    public string? Title { get; set; }
    /// <summary>« immediate » ou « end ».</summary>
    public string Feedback { get; set; } = "immediate";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int DurationSeconds { get; set; }
    public List<SyncItem> Items { get; set; } = [];
}

public class SyncItem
{
    public int QuestionId { get; set; }
    /// <summary>Numéros d'ordre des options choisies (stables même si la question a été réimportée).</summary>
    public List<int> Answer { get; set; } = [];
    public DateTime? AnsweredUtc { get; set; }
    public int TimeSeconds { get; set; }
    public bool Flagged { get; set; }
    public bool Doubt { get; set; }
}

public class SyncNote
{
    public string ClientId { get; set; } = "";
    /// <summary>Identifiant serveur ; absent pour une note créée hors ligne.</summary>
    public int? Id { get; set; }
    public int? Page { get; set; }
    public string Content { get; set; } = "";
    public string? Tags { get; set; }
    public int? ChapterId { get; set; }
    public string? EcoTask { get; set; }
    public bool Deleted { get; set; }
    /// <summary>Date de modification serveur connue au téléchargement : sert à détecter une modification concurrente.</summary>
    public DateTime? BaseUpdatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class SyncErrorEdit
{
    public int Id { get; set; }
    public string? Why { get; set; }
    public string? Trap { get; set; }
    public string? MindsetReflex { get; set; }
    /// <summary>Valeurs téléchargées, pour une fusion à trois voies.</summary>
    public string? BaseWhy { get; set; }
    public string? BaseTrap { get; set; }
    public string? BaseMindsetReflex { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Revue manuelle d'une erreur externe : « je savais » ou non.</summary>
public class SyncErrorReview
{
    public string ClientId { get; set; } = "";
    public int Id { get; set; }
    public bool KnewIt { get; set; }
    public DateTime AtUtc { get; set; }
}

public class SyncReading
{
    public string ClientId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int PageFrom { get; set; }
    public int PageTo { get; set; }
    public double Minutes { get; set; }
}

public record SyncNoteMapping(string ClientId, int Id, DateTime UpdatedUtc);

public class SyncResult
{
    public int SessionsImported { get; set; }
    public int SessionsAlreadyKnown { get; set; }
    public int NotesCreated { get; set; }
    public int NotesUpdated { get; set; }
    public int NotesDeleted { get; set; }
    public int ErrorsUpdated { get; set; }
    public int ErrorReviews { get; set; }
    public int Readings { get; set; }
    public List<SyncNoteMapping> NoteIds { get; set; } = [];
    /// <summary>Conflits résolus sans perte : expliqués en clair pour le journal de synchronisation.</summary>
    public List<string> Conflicts { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class SyncException(string message) : Exception(message);

/// <summary>
/// Mode hors ligne : prépare le paquet de données que l'appareil garde en local, puis intègre ce qui a été fait
/// sans connexion (sessions, notes, carnet d'erreurs, lecture). Le serveur reste l'arbitre : il recorrige
/// les réponses et applique la répétition espacée. Rejouer le même envoi ne crée aucun doublon.
/// </summary>
public class OfflineSyncService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock, StudyService study, TimeZoneSetting timeZone)
{
    public const int PackVersion = 1;
    public const string OfflineSuffix = " (hors ligne)";
    private const int MaxSessionsPerPush = 200;
    private const int MaxItemsPerSession = 400;

    public async Task<OfflinePack> BuildPackAsync(string? deviceId = null, string? deviceName = null, string? userAgent = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var ecoTasks = await db.EcoTasks.AsNoTracking().OrderBy(t => t.Domain).ThenBy(t => t.Id).ToListAsync(ct);
        var chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.StartPage).ToListAsync(ct);
        var plan = await db.PlanDays.AsNoTracking().Include(d => d.Chapters).Include(d => d.EcoTasks).OrderBy(d => d.Date).ToListAsync(ct);
        var questions = await db.Questions.AsNoTracking().Where(q => q.IsActive)
            .Include(q => q.Options).Include(q => q.EcoTask).Include(q => q.Scenario)
            .AsSplitQuery().OrderBy(q => q.Id).ToListAsync(ct);
        var errors = await db.ErrorEntries.AsNoTracking().Include(e => e.EcoTask).OrderBy(e => e.Id).ToListAsync(ct);
        var notes = await db.Notes.AsNoTracking().Include(n => n.EcoTask).OrderBy(n => n.Id).ToListAsync(ct);
        var seen = await db.SessionItems.Where(i => i.AnsweredUtc != null).Select(i => i.QuestionId).Distinct().ToListAsync(ct);
        var pdfLength = await db.CourseFiles.Where(f => f.Kind == DbPdfStore.Kind).Select(f => (long?)f.Length).FirstOrDefaultAsync(ct);

        var pack = new OfflinePack(
            PackVersion,
            clock.UtcNow,
            clock.Today,
            IanaId(timeZone.Current),
            await study.ExamDateAsync(ct),
            await study.SimulationDatesAsync(ct),
            int.TryParse(settings.GetValueOrDefault(SettingKeys.PageOffset), out var offset) ? offset : 0,
            int.TryParse(settings.GetValueOrDefault(SettingKeys.LastPage), out var lastPage) ? lastPage : null,
            pdfLength is { } length
                ? new OfflinePdfInfo(length,
                    int.TryParse(settings.GetValueOrDefault(SettingKeys.PdfPageCount), out var pages) ? pages : null,
                    settings.GetValueOrDefault(SettingKeys.PdfUploadedUtc))
                : null,
            ecoTasks.Select(t => new OfflineEcoTask(t.Code, t.Domain.ToString(), t.Title)).ToList(),
            chapters.Select(c => new OfflineChapter(c.Id, c.Code, c.Title, c.StartPage, c.EndPage)).ToList(),
            plan.Select(d => new OfflinePlanDay(d.Date, d.Week, d.PageFrom, d.PageTo, d.QuestionGoal, d.Label, d.Status.ToString(),
                d.Chapters.OrderBy(c => c.SortOrder).Select(c => c.Code).ToList(), d.EcoTasks.Select(t => t.Code).ToList())).ToList(),
            questions.Select(q => new OfflineQuestion(q.Id, q.Type.ToString(), q.Stem, q.Scenario?.Text, q.Approach.ToString(),
                q.EcoTask?.Code, q.EcoTask?.Domain.ToString(), q.ChapterId, q.Source, q.Explanation,
                q.Options.OrderBy(o => o.Order).Select(o => new OfflineOption(o.Order, o.Text, o.IsCorrect)).ToList())).ToList(),
            errors.Select(e => new OfflineError(e.Id, e.QuestionId, e.EcoTask?.Code, e.Summary, e.Why, e.Trap, e.MindsetReflex,
                e.Status.ToString(), e.Origin.ToString(), e.ExternalSource, e.NextReview, e.IntervalStep, e.ConsecutiveCorrect,
                e.ReviewCount, e.TimesWrong, e.UpdatedUtc)).ToList(),
            notes.Select(n => new OfflineNote(n.Id, n.ClientId, n.Page, n.Content, n.Tags, n.ChapterId, n.EcoTask?.Code, n.CreatedUtc, n.UpdatedUtc)).ToList(),
            seen);

        if (!string.IsNullOrWhiteSpace(deviceId))
            await TouchDeviceAsync(deviceId, deviceName, userAgent, pull: true, ct);
        return pack;
    }

    public async Task<SyncResult> ApplyAsync(SyncPush push, string? userAgent = null, CancellationToken ct = default)
    {
        Validate(push);
        await using var probe = await dbFactory.CreateDbContextAsync(ct);
        var strategy = probe.Database.CreateExecutionStrategy();
        // Tout ou rien : un envoi interrompu se rejoue entièrement, les identifiants client évitent les doublons.
        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var r = new SyncResult();
            await ApplySessionsAsync(db, push, r, ct);
            await ApplyErrorReviewsAsync(db, push, r, ct);
            await ApplyErrorEditsAsync(db, push, r, ct);
            await ApplyNotesAsync(db, push, r, ct);
            await ApplyReadingsAsync(db, push, r, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return r;
        });

        if (push.LastPage is > 0 and var page) await study.SetLastPageAsync(page, ct);
        await TouchDeviceAsync(push.DeviceId, push.DeviceName, userAgent, pull: false, ct);
        return result;
    }

    public async Task<List<SyncDevice>> DevicesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SyncDevices.AsNoTracking().OrderByDescending(d => d.LastPushUtc ?? d.LastPullUtc ?? d.CreatedUtc).ToListAsync(ct);
    }

    public async Task ForgetDeviceAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.SyncDevices.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
    }

    // ---------- Sessions ----------

    private async Task ApplySessionsAsync(AppDbContext db, SyncPush push, SyncResult r, CancellationToken ct)
    {
        if (push.Sessions.Count == 0) return;
        var clientIds = push.Sessions.Select(s => s.ClientId).ToList();
        var known = (await db.Sessions.Where(s => s.ClientId != null && clientIds.Contains(s.ClientId)).Select(s => s.ClientId!).ToListAsync(ct)).ToHashSet();
        var questionIds = push.Sessions.SelectMany(s => s.Items).Select(i => i.QuestionId).Distinct().ToList();
        var questions = await db.Questions.Include(q => q.Options).Where(q => questionIds.Contains(q.Id)).AsSplitQuery().ToDictionaryAsync(q => q.Id, ct);
        var now = clock.UtcNow;

        // Dans l'ordre chronologique : la répétition espacée suit l'ordre réel des révisions.
        foreach (var dto in push.Sessions.OrderBy(s => s.EndUtc))
        {
            if (known.Contains(dto.ClientId)) { r.SessionsAlreadyKnown++; continue; }
            var end = dto.EndUtc > now ? now : dto.EndUtc;
            var start = dto.StartUtc > end ? end : dto.StartUtc;
            var type = dto.Type.Equals("replay", StringComparison.OrdinalIgnoreCase) ? SessionType.ErrorReplay : SessionType.Training;
            var session = new StudySession
            {
                ClientId = dto.ClientId,
                Type = type,
                FeedbackMode = dto.Feedback.Equals("end", StringComparison.OrdinalIgnoreCase) ? FeedbackMode.AtEnd : FeedbackMode.Immediate,
                StartUtc = start,
                EndUtc = end,
                DurationSeconds = Math.Max(dto.DurationSeconds, 0),
                DeadlineUtc = start.AddSeconds(Math.Max(dto.DurationSeconds, 0)),
                FiltersJson = """{"offline":true}""",
                Title = Truncate((string.IsNullOrWhiteSpace(dto.Title) ? "Entraînement" : dto.Title.Trim()) + OfflineSuffix, 200),
            };

            var order = 0;
            var missing = 0;
            foreach (var item in dto.Items)
            {
                if (!questions.TryGetValue(item.QuestionId, out var question)) { missing++; continue; }
                var selected = question.Options.Where(o => item.Answer.Contains(o.Order)).Select(o => o.Id).ToList();
                session.Items.Add(new SessionItem
                {
                    QuestionId = question.Id,
                    Question = question,
                    Order = ++order,
                    SectionIndex = 1,
                    AnswerJson = Scoring.SerializeAnswer(selected),
                    AnsweredUtc = selected.Count > 0 ? Clamp(item.AnsweredUtc ?? end, start, end) : null,
                    IsCorrect = Scoring.IsCorrect(question, selected),
                    Flagged = item.Flagged,
                    Doubt = item.Doubt,
                    TimeSeconds = Math.Clamp(item.TimeSeconds, 0, 3600),
                });
            }
            if (missing > 0)
                r.Warnings.Add($"« {dto.Title} » : {missing} question(s) supprimée(s) de la banque depuis le téléchargement, ignorée(s).");
            if (session.Items.Count == 0) continue;

            session.CorrectCount = session.Items.Count(i => i.IsCorrect == true);
            session.ScorePercent = Math.Round(100.0 * session.CorrectCount.Value / session.Items.Count, 1);
            db.Sessions.Add(session);
            var day = DateOnly.FromDateTime(clock.ToLocal(end));
            await ErrorLogService.ApplySessionAsync(db, session, day, now, ct);
            // Enregistré tout de suite : la session suivante doit voir les entrées du carnet créées par celle-ci.
            await db.SaveChangesAsync(ct);
            known.Add(dto.ClientId);
            r.SessionsImported++;
        }
    }

    // ---------- Carnet d'erreurs ----------

    private async Task ApplyErrorReviewsAsync(AppDbContext db, SyncPush push, SyncResult r, CancellationToken ct)
    {
        if (push.ErrorReviews.Count == 0) return;
        // Une revue déjà appliquée laisse une trace dans les réglages : un renvoi ne la compte pas deux fois.
        var keys = push.ErrorReviews.Select(v => ReviewKey(v.ClientId)).ToList();
        var applied = (await db.Settings.Where(s => keys.Contains(s.Key)).Select(s => s.Key).ToListAsync(ct)).ToHashSet();
        var ids = push.ErrorReviews.Select(v => v.Id).Distinct().ToList();
        var entries = await db.ErrorEntries.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var review in push.ErrorReviews.OrderBy(v => v.AtUtc))
        {
            var key = ReviewKey(review.ClientId);
            if (applied.Contains(key)) continue;
            if (!entries.TryGetValue(review.Id, out var entry))
            {
                r.Warnings.Add("Une revue porte sur une erreur supprimée en ligne entre-temps : ignorée.");
                continue;
            }
            var day = DateOnly.FromDateTime(clock.ToLocal(review.AtUtc > clock.UtcNow ? clock.UtcNow : review.AtUtc));
            if (review.KnewIt) SpacedRepetition.RecordSuccess(entry, day);
            else SpacedRepetition.RecordMiss(entry, day, isReview: true);
            entry.UpdatedUtc = clock.UtcNow;
            db.Settings.Add(new AppSetting { Key = key, Value = review.AtUtc.ToString("O", CultureInfo.InvariantCulture) });
            applied.Add(key);
            r.ErrorReviews++;
        }
    }

    private static string ReviewKey(string clientId) => "OfflineReview:" + Truncate(clientId, 80);

    private async Task ApplyErrorEditsAsync(AppDbContext db, SyncPush push, SyncResult r, CancellationToken ct)
    {
        if (push.ErrorEdits.Count == 0) return;
        var ids = push.ErrorEdits.Select(e => e.Id).Distinct().ToList();
        var entries = await db.ErrorEntries.Include(e => e.Question).Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var edit in push.ErrorEdits)
        {
            if (!entries.TryGetValue(edit.Id, out var entry))
            {
                r.Warnings.Add("Une analyse d'erreur porte sur une entrée supprimée en ligne entre-temps : ignorée.");
                continue;
            }
            var changed = false;
            changed |= Merge(entry.Why, edit.BaseWhy, edit.Why, v => entry.Why = v, "pourquoi", entry, edit, r);
            changed |= Merge(entry.Trap, edit.BaseTrap, edit.Trap, v => entry.Trap = v, "piège", entry, edit, r);
            changed |= Merge(entry.MindsetReflex, edit.BaseMindsetReflex, edit.MindsetReflex, v => entry.MindsetReflex = v, "réflexe", entry, edit, r);
            if (changed)
            {
                entry.UpdatedUtc = clock.UtcNow;
                r.ErrorsUpdated++;
            }
        }
    }

    /// <summary>
    /// Fusion à trois voies d'un champ : si la version en ligne n'a pas bougé depuis le téléchargement, la version
    /// hors ligne s'applique. Si les deux ont changé, les deux textes sont gardés l'un sous l'autre.
    /// </summary>
    private static bool Merge(string? server, string? baseline, string? local, Action<string?> set, string field, ErrorEntry entry, SyncErrorEdit edit, SyncResult r)
    {
        var s = Norm(server); var b = Norm(baseline); var l = Norm(local);
        if (l == s || l == b) return false;            // rien de nouveau, ou déjà appliqué
        if (l is not null && s?.Contains(l, StringComparison.Ordinal) == true) return false; // conflit déjà fusionné (envoi rejoué)
        if (s == b) { set(l); return true; }           // seul l'appareil a modifié le champ
        set($"{s}\n\n[hors ligne {edit.UpdatedUtc:dd/MM HH:mm} UTC] {l}");
        r.Conflicts.Add($"Erreur « {Shorten(entry.Question?.Stem ?? entry.Summary)} » : « {field} » modifié en ligne et hors ligne, les deux textes sont conservés.");
        return true;
    }

    // ---------- Notes ----------

    private async Task ApplyNotesAsync(AppDbContext db, SyncPush push, SyncResult r, CancellationToken ct)
    {
        if (push.Notes.Count == 0) return;
        var ecoTasks = await db.EcoTasks.AsNoTracking().ToDictionaryAsync(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase, ct);
        var chapterIds = (await db.Chapters.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        var now = clock.UtcNow;

        foreach (var dto in push.Notes)
        {
            Note? note = null;
            if (dto.Id is { } id) note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
            note ??= await db.Notes.FirstOrDefaultAsync(n => n.ClientId == dto.ClientId, ct);
            var tags = string.Join(", ", NoteService.SplitTags(dto.Tags));
            var chapterId = dto.ChapterId is { } c && chapterIds.Contains(c) ? c : (int?)null;
            var ecoTaskId = dto.EcoTask is { } code && ecoTasks.TryGetValue(code, out var t) ? t : (int?)null;

            if (note is null)
            {
                if (dto.Deleted) continue;
                if (dto.Id is not null)
                    r.Conflicts.Add($"Note « {Shorten(dto.Content)} » supprimée en ligne mais modifiée hors ligne : recréée.");
                note = NewNote(dto, tags, chapterId, ecoTaskId, now);
                db.Notes.Add(note);
                await db.SaveChangesAsync(ct);
                r.NotesCreated++;
                r.NoteIds.Add(new(dto.ClientId, note.Id, note.UpdatedUtc));
                continue;
            }

            var sameContent = note.Content == dto.Content && note.Tags == tags && note.Page == dto.Page;
            var untouchedOnline = dto.BaseUpdatedUtc is { } baseline ? note.UpdatedUtc <= baseline.AddSeconds(1) : note.ClientId == dto.ClientId;
            if (dto.Deleted)
            {
                if (untouchedOnline || sameContent)
                {
                    db.Notes.Remove(note);
                    r.NotesDeleted++;
                }
                else r.Conflicts.Add($"Note « {Shorten(note.Content)} » supprimée hors ligne mais modifiée en ligne : gardée.");
                continue;
            }

            if (sameContent)
            {
                r.NoteIds.Add(new(dto.ClientId, note.Id, note.UpdatedUtc));
                continue;
            }
            if (untouchedOnline)
            {
                note.Content = dto.Content;
                note.Tags = tags;
                note.Page = dto.Page;
                note.ChapterId = chapterId;
                note.EcoTaskId = ecoTaskId;
                note.UpdatedUtc = now;
                r.NotesUpdated++;
                r.NoteIds.Add(new(dto.ClientId, note.Id, note.UpdatedUtc));
                continue;
            }

            // Modifiée des deux côtés : on garde la version en ligne et on ajoute la version hors ligne à côté.
            var copy = NewNote(dto, string.Join(", ", NoteService.SplitTags(tags + ", conflit")), chapterId, ecoTaskId, now);
            copy.ClientId = dto.ClientId + "-conflit";
            if (!await db.Notes.AnyAsync(n => n.ClientId == copy.ClientId, ct))
            {
                db.Notes.Add(copy);
                await db.SaveChangesAsync(ct);
                r.NotesCreated++;
                r.Conflicts.Add($"Note « {Shorten(dto.Content)} » modifiée en ligne et hors ligne : la version hors ligne est ajoutée à côté (étiquette « conflit »).");
            }
        }
    }

    private static Note NewNote(SyncNote dto, string tags, int? chapterId, int? ecoTaskId, DateTime now) => new()
    {
        ClientId = Truncate(dto.ClientId, 64),
        Page = dto.Page,
        Content = dto.Content,
        Tags = Truncate(tags, 400),
        ChapterId = chapterId,
        EcoTaskId = ecoTaskId,
        CreatedUtc = dto.UpdatedUtc > now ? now : dto.UpdatedUtc,
        UpdatedUtc = now,
    };

    // ---------- Lecture ----------

    private async Task ApplyReadingsAsync(AppDbContext db, SyncPush push, SyncResult r, CancellationToken ct)
    {
        if (push.Readings.Count == 0) return;
        var ids = push.Readings.Select(x => x.ClientId).ToList();
        var existing = await db.ReadingLogs.Where(l => l.ClientId != null && ids.Contains(l.ClientId)).ToDictionaryAsync(l => l.ClientId!, ct);
        var planDays = await db.PlanDays.AsNoTracking().ToDictionaryAsync(d => d.Date, d => d.Id, ct);
        foreach (var dto in push.Readings)
        {
            if (dto.PageFrom < 1 || dto.PageTo < dto.PageFrom) continue;
            if (!existing.TryGetValue(dto.ClientId, out var log))
            {
                var day = DateOnly.FromDateTime(clock.ToLocal(dto.StartedUtc > clock.UtcNow ? clock.UtcNow : dto.StartedUtc));
                log = new ReadingLog
                {
                    ClientId = Truncate(dto.ClientId, 64),
                    Date = day,
                    StartedUtc = dto.StartedUtc,
                    PageFrom = dto.PageFrom,
                    PageTo = dto.PageTo,
                    StudyPlanDayId = planDays.TryGetValue(day, out var planDayId) ? planDayId : null,
                };
                db.ReadingLogs.Add(log);
                existing[dto.ClientId] = log;
            }
            log.PageFrom = Math.Min(log.PageFrom, dto.PageFrom);
            log.PageTo = Math.Max(log.PageTo, dto.PageTo);
            log.Minutes = Math.Max(log.Minutes, Math.Round(Math.Clamp(dto.Minutes, 0, 24 * 60), 1));
            log.UpdatedUtc = dto.UpdatedUtc > clock.UtcNow ? clock.UtcNow : dto.UpdatedUtc;
            r.Readings++;
        }
    }

    // ---------- Appareils ----------

    private async Task TouchDeviceAsync(string deviceId, string? name, string? userAgent, bool pull, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = Truncate(deviceId.Trim(), 64);
        var device = await db.SyncDevices.FirstOrDefaultAsync(d => d.DeviceId == id, ct);
        if (device is null)
        {
            device = new SyncDevice { DeviceId = id, CreatedUtc = clock.UtcNow };
            db.SyncDevices.Add(device);
        }
        if (!string.IsNullOrWhiteSpace(name)) device.Name = Truncate(name.Trim(), 100);
        if (!string.IsNullOrWhiteSpace(userAgent)) device.UserAgent = Truncate(userAgent, 400);
        if (pull) device.LastPullUtc = clock.UtcNow;
        else device.LastPushUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ---------- Aides ----------

    private static void Validate(SyncPush push)
    {
        if (string.IsNullOrWhiteSpace(push.DeviceId)) throw new SyncException("Identifiant d'appareil manquant.");
        if (push.Sessions.Count > MaxSessionsPerPush) throw new SyncException($"Trop de sessions dans un seul envoi (maximum {MaxSessionsPerPush}).");
        if (push.Sessions.Any(s => string.IsNullOrWhiteSpace(s.ClientId) || s.Items.Count > MaxItemsPerSession))
            throw new SyncException("Session hors ligne invalide.");
        if (push.Notes.Any(n => string.IsNullOrWhiteSpace(n.ClientId)) || push.ErrorReviews.Any(v => string.IsNullOrWhiteSpace(v.ClientId))
            || push.Readings.Any(x => string.IsNullOrWhiteSpace(x.ClientId)))
            throw new SyncException("Modification hors ligne sans identifiant.");
    }

    private static string IanaId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId) return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "UTC";
    }

    private static DateTime Clamp(DateTime value, DateTime min, DateTime max) => value < min ? min : value > max ? max : value;
    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Shorten(string? s) => s is null ? "" : s.Length > 60 ? s[..57] + "…" : s;
}
