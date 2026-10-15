using Microsoft.EntityFrameworkCore;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure.Data;

namespace PmpStudy.Infrastructure.Services;

public enum ErrorFilter { All, Due, Incomplete, Active, Mastered }

public class ErrorLogService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock)
{
    /// <summary>
    /// Alimente le carnet à la clôture d'une session : une entrée par question, créée pour toute réponse
    /// fausse ou marquée « doute », mise à jour si l'erreur se répète ; une bonne réponse fait avancer la revue.
    /// </summary>
    public static async Task ApplySessionAsync(AppDbContext db, StudySession session, DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var questionIds = session.Items.Select(i => i.QuestionId).Distinct().ToList();
        var existing = await db.ErrorEntries.Where(e => e.QuestionId != null && questionIds.Contains(e.QuestionId.Value))
            .ToDictionaryAsync(e => e.QuestionId!.Value, ct);

        foreach (var item in session.Items)
        {
            var miss = item.IsCorrect != true || item.Doubt;
            if (existing.TryGetValue(item.QuestionId, out var entry))
            {
                if (miss) SpacedRepetition.RecordMiss(entry, today, isReview: true);
                else if (entry.Status == ErrorStatus.Active) SpacedRepetition.RecordSuccess(entry, today);
                else continue;
                entry.UpdatedUtc = nowUtc;
            }
            else if (miss)
            {
                entry = SpacedRepetition.CreateFromMiss(today, nowUtc);
                entry.QuestionId = item.QuestionId;
                entry.EcoTaskId = item.Question?.EcoTaskId;
                db.ErrorEntries.Add(entry);
                existing[item.QuestionId] = entry;
            }
        }
    }

    public async Task<List<ErrorEntry>> ListAsync(ErrorFilter filter, string? search = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = clock.Today;
        var query = db.ErrorEntries.AsNoTracking()
            .Include(e => e.EcoTask)
            .Include(e => e.Question).ThenInclude(q => q!.Options)
            .AsQueryable();
        query = filter switch
        {
            ErrorFilter.Due => query.Where(e => e.Status == ErrorStatus.Active && e.NextReview <= today),
            ErrorFilter.Incomplete => query.Where(e => e.Why == null || e.Why == "" || e.Trap == null || e.Trap == ""),
            ErrorFilter.Active => query.Where(e => e.Status == ErrorStatus.Active),
            ErrorFilter.Mastered => query.Where(e => e.Status == ErrorStatus.Mastered),
            _ => query,
        };
        var list = await query.OrderBy(e => e.Status).ThenBy(e => e.NextReview).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            list = list.Where(e =>
                Contains(e.Summary, s) || Contains(e.Why, s) || Contains(e.Trap, s) || Contains(e.MindsetReflex, s) ||
                Contains(e.Question?.Stem, s) || Contains(e.EcoTask?.Code, s)).ToList();
        }
        return list;
    }

    private static bool Contains(string? text, string s) => text?.Contains(s, StringComparison.OrdinalIgnoreCase) == true;

    public async Task<int> CountDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = clock.Today;
        return await db.ErrorEntries.CountAsync(e => e.Status == ErrorStatus.Active && e.NextReview <= today, ct);
    }

    public async Task<(int Due, int Incomplete, int Active, int Mastered)> CountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = clock.Today;
        return (
            await db.ErrorEntries.CountAsync(e => e.Status == ErrorStatus.Active && e.NextReview <= today, ct),
            await db.ErrorEntries.CountAsync(e => e.Why == null || e.Why == "" || e.Trap == null || e.Trap == "", ct),
            await db.ErrorEntries.CountAsync(e => e.Status == ErrorStatus.Active, ct),
            await db.ErrorEntries.CountAsync(e => e.Status == ErrorStatus.Mastered, ct));
    }

    public async Task SaveDetailsAsync(int id, string? why, string? trap, string? reflex, int? ecoTaskId, string? summary, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ErrorEntries.FindAsync([id], ct) ?? throw new InvalidOperationException("Entrée introuvable.");
        entry.Why = why?.Trim();
        entry.Trap = trap?.Trim();
        entry.MindsetReflex = reflex?.Trim();
        entry.EcoTaskId = ecoTaskId;
        if (entry.Origin == ErrorOrigin.External || summary is not null) entry.Summary = summary?.Trim();
        entry.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saisie manuelle d'une erreur faite ailleurs (PMI Study Hall, livre…).</summary>
    public async Task<int> AddExternalAsync(string summary, string? source, int? ecoTaskId, string? why, string? trap, string? reflex, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = SpacedRepetition.CreateFromMiss(clock.Today, clock.UtcNow);
        entry.Origin = ErrorOrigin.External;
        entry.Summary = summary.Trim();
        entry.ExternalSource = source?.Trim();
        entry.EcoTaskId = ecoTaskId;
        entry.Why = why?.Trim();
        entry.Trap = trap?.Trim();
        entry.MindsetReflex = reflex?.Trim();
        db.ErrorEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry.Id;
    }

    /// <summary>Revue manuelle d'une erreur externe (pas de question à rejouer) : « je savais » ou « je ne savais pas ».</summary>
    public async Task ReviewManuallyAsync(int id, bool knewIt, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ErrorEntries.FindAsync([id], ct) ?? throw new InvalidOperationException("Entrée introuvable.");
        if (knewIt) SpacedRepetition.RecordSuccess(entry, clock.Today);
        else SpacedRepetition.RecordMiss(entry, clock.Today, isReview: true);
        entry.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReactivateAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entry = await db.ErrorEntries.FindAsync([id], ct) ?? throw new InvalidOperationException("Entrée introuvable.");
        SpacedRepetition.RecordMiss(entry, clock.Today, isReview: false);
        entry.TimesWrong--; // réactivation volontaire, pas une nouvelle erreur
        entry.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ErrorEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
    }
}
