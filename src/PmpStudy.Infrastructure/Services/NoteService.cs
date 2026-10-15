using System.Text;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Infrastructure.Data;

namespace PmpStudy.Infrastructure.Services;

public class NoteQuery
{
    public int? Page { get; set; }
    public int? ChapterId { get; set; }
    public int? EcoTaskId { get; set; }
    public string? Tag { get; set; }
    public string? Text { get; set; }
}

public class NoteService(IDbContextFactory<AppDbContext> dbFactory, IStudyClock clock)
{
    public async Task<List<Note>> ListAsync(NoteQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Notes.AsNoTracking().Include(n => n.Chapter).Include(n => n.EcoTask).AsQueryable();
        if (query.Page is { } page) q = q.Where(n => n.Page == page);
        if (query.ChapterId is { } chapterId) q = q.Where(n => n.ChapterId == chapterId);
        if (query.EcoTaskId is { } taskId) q = q.Where(n => n.EcoTaskId == taskId);
        var list = await q.OrderBy(n => n.Page).ThenBy(n => n.CreatedUtc).ToListAsync(ct);

        // Recherche plein texte et étiquettes en mémoire : insensible à la casse quel que soit le fournisseur SQL.
        if (!string.IsNullOrWhiteSpace(query.Tag))
            list = list.Where(n => SplitTags(n.Tags).Contains(query.Tag.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list = list.Where(n => words.All(w =>
                n.Content.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                n.Tags.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                (n.Chapter?.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        }
        return list;
    }

    /// <summary>Notes de la page et du chapitre qui la contient.</summary>
    public async Task<List<Note>> ForPageAsync(int page, int? chapterId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notes.AsNoTracking().Include(n => n.EcoTask).Include(n => n.Chapter)
            .Where(n => n.Page == page || (chapterId != null && n.ChapterId == chapterId))
            .OrderBy(n => n.Page == page ? 0 : 1).ThenBy(n => n.Page).ThenBy(n => n.CreatedUtc)
            .ToListAsync(ct);
    }

    public async Task<List<string>> AllTagsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tags = await db.Notes.Select(n => n.Tags).ToListAsync(ct);
        return tags.SelectMany(SplitTags).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
    }

    public async Task<Note> SaveAsync(Note input, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var note = input.Id == 0 ? null : await db.Notes.FindAsync([input.Id], ct);
        if (note is null)
        {
            note = new Note { CreatedUtc = now };
            db.Notes.Add(note);
        }
        note.Page = input.Page;
        note.Content = input.Content ?? "";
        note.Tags = string.Join(", ", SplitTags(input.Tags));
        note.ChapterId = input.ChapterId;
        note.EcoTaskId = input.EcoTaskId;
        note.UpdatedUtc = now;
        await db.SaveChangesAsync(ct);
        return note;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notes.Where(n => n.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<Note?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notes.AsNoTracking().Include(n => n.Chapter).Include(n => n.EcoTask).FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    public static IEnumerable<string> SplitTags(string? tags) =>
        (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Export Markdown, regroupé par chapitre puis par page.</summary>
    public static string ToMarkdown(IEnumerable<Note> notes, string title = "Notes PMP")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        foreach (var group in notes.GroupBy(n => n.Chapter).OrderBy(g => g.Key?.SortOrder ?? int.MaxValue))
        {
            sb.AppendLine(group.Key is null ? "## Sans chapitre" : $"## {group.Key.Code} — {group.Key.Title}").AppendLine();
            foreach (var note in group.OrderBy(n => n.Page))
            {
                var meta = new List<string>();
                if (note.Page is { } p) meta.Add($"p. {p}");
                if (note.EcoTask is not null) meta.Add(note.EcoTask.Code);
                if (!string.IsNullOrWhiteSpace(note.Tags)) meta.Add($"tags : {note.Tags}");
                sb.AppendLine($"### {(meta.Count > 0 ? string.Join(" · ", meta) : "Note")}").AppendLine();
                sb.AppendLine(note.Content.Trim()).AppendLine();
            }
        }
        return sb.ToString();
    }
}
