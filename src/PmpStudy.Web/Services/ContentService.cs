using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Model.Rules;
using PmpStudy.Web.Model;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig;

namespace PmpStudy.Web.Services;

/// <summary>Banque de questions, chapitres, tâches ECO et plan de révision : lecture et imports.</summary>
public partial class ContentService(IDbContextFactory<AppDbContext> dbFactory)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------- Référentiels ----------

    public async Task<List<EcoTask>> EcoTasksAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var list = await db.EcoTasks.AsNoTracking().ToListAsync(ct);
        return list.OrderBy(t => t.Domain).ThenBy(t => EcoCatalog.SortKey(t.Code)).ToList();
    }

    public async Task<List<Chapter>> ChaptersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.StartPage).ToListAsync(ct);
    }

    public async Task<Chapter?> ChapterForPageAsync(int page, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Le plus profond (plus petite plage) qui contient la page.
        return await db.Chapters.AsNoTracking()
            .Where(c => c.StartPage <= page && c.EndPage >= page)
            .OrderBy(c => c.EndPage - c.StartPage)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<string>> SourcesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Questions.Select(q => q.Source).Distinct().OrderBy(s => s).ToListAsync(ct);
    }

    public async Task<QuestionBankStats> BankStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Questions.Where(q => q.IsActive)
            .Select(q => new { Domain = q.EcoTask != null ? (Domain?)q.EcoTask.Domain : null, q.Approach, q.Source })
            .ToListAsync(ct);
        return new QuestionBankStats(
            rows.Count,
            rows.GroupBy(r => r.Domain).ToDictionary(g => g.Key?.ToString() ?? "—", g => g.Count()),
            rows.GroupBy(r => r.Approach).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            rows.GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Count()));
    }

    public async Task<List<Question>> SearchQuestionsAsync(string? text, string? source, int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Questions.AsNoTracking().Include(q => q.Options).Include(q => q.EcoTask).Include(q => q.Chapter).AsQueryable();
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(q => q.Source == source);
        if (!string.IsNullOrWhiteSpace(text)) query = query.Where(q => q.Stem.Contains(text) || q.Explanation.Contains(text));
        return await query.OrderBy(q => q.Id).Take(take).AsSplitQuery().ToListAsync(ct);
    }

    public async Task SetQuestionActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Questions.Where(q => q.Id == id).ExecuteUpdateAsync(s => s.SetProperty(q => q.IsActive, active), ct);
    }

    // ---------- Import des questions ----------

    public static List<QuestionImport> ParseQuestions(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            // Accepte aussi { "questions": [...] } ou une question seule.
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("questions", out var arr))
                return arr.Deserialize<List<QuestionImport>>(Json) ?? [];
            return [doc.RootElement.Deserialize<QuestionImport>(Json)!];
        }
        return JsonSerializer.Deserialize<List<QuestionImport>>(json, Json) ?? [];
    }

    public async Task<ImportResult> ImportQuestionsAsync(IReadOnlyList<QuestionImport> items, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tasks = await db.EcoTasks.ToDictionaryAsync(t => t.Code.ToUpperInvariant(), ct);
        var chapters = await db.Chapters.ToDictionaryAsync(c => c.Code, ct);
        var scenarios = new Dictionary<string, Scenario>();
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        for (var n = 0; n < items.Count; n++)
        {
            var dto = items[n];
            var label = dto.Id ?? $"#{n + 1}";
            var problem = Validate(dto, tasks, out var type, out var approach);
            if (problem is not null) { errors.Add($"{label} : {problem}"); skipped++; continue; }

            EcoTask? task = dto.EcoTask is null ? null : tasks[dto.EcoTask.Trim().ToUpperInvariant()];
            Chapter? chapter = null;
            if (!string.IsNullOrWhiteSpace(dto.Chapter) && !chapters.TryGetValue(dto.Chapter.Trim(), out chapter))
                errors.Add($"{label} : chapitre « {dto.Chapter} » inconnu, question importée sans chapitre.");

            Scenario? scenario = null;
            if (!string.IsNullOrWhiteSpace(dto.Scenario))
            {
                var key = dto.Scenario.Trim();
                if (!scenarios.TryGetValue(key, out scenario))
                {
                    scenario = await db.Scenarios.FirstOrDefaultAsync(s => s.Text == key, ct) ?? new Scenario { Text = key };
                    scenarios[key] = scenario;
                }
            }

            var question = dto.Id is null ? null
                : await db.Questions.Include(q => q.Options).FirstOrDefaultAsync(q => q.ExternalId == dto.Id, ct);
            var isNew = question is null;
            question ??= new Question { ExternalId = dto.Id };
            question.Type = type;
            question.Approach = approach;
            question.Stem = dto.Stem.Trim();
            question.Explanation = dto.Explanation.Trim();
            question.Source = string.IsNullOrWhiteSpace(dto.Source) ? "import" : dto.Source.Trim();
            question.Difficulty = Math.Clamp(dto.Difficulty, 1, 3);
            question.Language = string.IsNullOrWhiteSpace(dto.Language) ? "fr" : dto.Language.Trim();
            question.EcoTask = task;
            question.Chapter = chapter;
            question.Scenario = scenario;

            if (isNew)
            {
                question.Options = dto.Options.Select((o, i) => new QuestionOption { Order = i + 1, Text = o.Text.Trim(), IsCorrect = o.Correct, MatchKey = o.MatchKey }).ToList();
                db.Questions.Add(question);
                created++;
            }
            else
            {
                // Mise à jour en place pour garder les identifiants d'options déjà utilisés dans les réponses.
                var existing = question.Options.OrderBy(o => o.Order).ToList();
                for (var i = 0; i < dto.Options.Count; i++)
                {
                    var o = dto.Options[i];
                    if (i < existing.Count) { existing[i].Text = o.Text.Trim(); existing[i].IsCorrect = o.Correct; existing[i].MatchKey = o.MatchKey; existing[i].Order = i + 1; }
                    else question.Options.Add(new QuestionOption { Order = i + 1, Text = o.Text.Trim(), IsCorrect = o.Correct, MatchKey = o.MatchKey });
                }
                foreach (var extra in existing.Skip(dto.Options.Count)) db.QuestionOptions.Remove(extra);
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated, skipped, errors);
    }

    private static string? Validate(QuestionImport dto, Dictionary<string, EcoTask> tasks, out QuestionType type, out Approach approach)
    {
        type = QuestionType.Single;
        approach = Approach.Predictive;
        if (string.IsNullOrWhiteSpace(dto.Stem)) return "énoncé vide";
        switch (dto.Type.Trim().ToLowerInvariant())
        {
            case "single": case "unique": type = QuestionType.Single; break;
            case "multiple": case "multi": type = QuestionType.Multiple; break;
            default: return $"type « {dto.Type} » non pris en charge (single ou multiple ; les autres types arrivent au lot 3)";
        }
        switch (dto.Approach.Trim().ToLowerInvariant())
        {
            case "predictive": case "prédictive": case "predictif": case "prédictif": approach = Approach.Predictive; break;
            case "agile": case "adaptive": case "adaptative": approach = Approach.Agile; break;
            case "hybrid": case "hybride": approach = Approach.Hybrid; break;
            default: return $"approche « {dto.Approach} » inconnue";
        }
        if (dto.EcoTask is not null && !tasks.ContainsKey(dto.EcoTask.Trim().ToUpperInvariant())) return $"tâche ECO « {dto.EcoTask} » inconnue";
        if (dto.Options.Count < 2) return "il faut au moins deux options";
        if (dto.Options.Any(o => string.IsNullOrWhiteSpace(o.Text))) return "option vide";
        var correct = dto.Options.Count(o => o.Correct);
        if (type == QuestionType.Single && correct != 1) return $"choix unique avec {correct} bonne(s) réponse(s)";
        if (type == QuestionType.Multiple && correct < 1) return "choix multiple sans bonne réponse";
        return null;
    }

    public async Task<List<QuestionImport>> ExportQuestionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var questions = await db.Questions.AsNoTracking().Include(q => q.Options).Include(q => q.EcoTask).Include(q => q.Chapter).Include(q => q.Scenario)
            .OrderBy(q => q.Id).AsSplitQuery().ToListAsync(ct);
        return questions.Select(q => new QuestionImport
        {
            Id = q.ExternalId ?? $"db-{q.Id}",
            Type = q.Type == QuestionType.Multiple ? "multiple" : "single",
            EcoTask = q.EcoTask?.Code,
            Approach = q.Approach.ToString().ToLowerInvariant(),
            Chapter = q.Chapter?.Code,
            Scenario = q.Scenario?.Text,
            Stem = q.Stem,
            Options = q.Options.OrderBy(o => o.Order).Select(o => new OptionImport { Text = o.Text, Correct = o.IsCorrect, MatchKey = o.MatchKey }).ToList(),
            Explanation = q.Explanation,
            Source = q.Source,
            Difficulty = q.Difficulty,
            Language = q.Language,
        }).ToList();
    }

    // ---------- Plan de révision ----------

    public async Task<ImportResult> ImportPlanAsync(IReadOnlyList<PlanDayImport> days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tasks = await db.EcoTasks.ToDictionaryAsync(t => t.Code.ToUpperInvariant(), ct);
        var chapters = await db.Chapters.ToDictionaryAsync(c => c.Code, ct);
        int created = 0, updated = 0;
        var errors = new List<string>();

        foreach (var dto in days)
        {
            var day = await db.PlanDays.Include(d => d.Chapters).Include(d => d.EcoTasks).FirstOrDefaultAsync(d => d.Date == dto.Date, ct);
            if (day is null) { day = new StudyPlanDay { Date = dto.Date }; db.PlanDays.Add(day); created++; }
            else updated++;
            day.Week = dto.Week;
            day.PageFrom = dto.PageFrom;
            day.PageTo = dto.PageTo;
            day.QuestionGoal = Math.Max(0, dto.QuestionGoal);
            day.Label = dto.Label;
            day.Chapters.Clear();
            foreach (var code in dto.Chapters)
                if (chapters.TryGetValue(code.Trim(), out var c)) day.Chapters.Add(c);
                else errors.Add($"{dto.Date:yyyy-MM-dd} : chapitre « {code} » inconnu (importe ou extrais d'abord les chapitres).");
            day.EcoTasks.Clear();
            foreach (var code in dto.EcoTasks)
                if (tasks.TryGetValue(code.Trim().ToUpperInvariant(), out var t)) day.EcoTasks.Add(t);
                else errors.Add($"{dto.Date:yyyy-MM-dd} : tâche ECO « {code} » inconnue.");
        }

        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated, 0, errors);
    }

    public async Task<List<StudyPlanDay>> PlanAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PlanDays.AsNoTracking().Include(d => d.Chapters).Include(d => d.EcoTasks)
            .OrderBy(d => d.Date).AsSplitQuery().ToListAsync(ct);
    }

    // ---------- Chapitres ----------

    public async Task<ImportResult> ImportChaptersAsync(IReadOnlyList<ChapterImport> items, bool replace, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (replace)
        {
            // Détache d'abord ce qui pointe vers les chapitres.
            await db.Notes.ExecuteUpdateAsync(s => s.SetProperty(n => n.ChapterId, (int?)null), ct);
            await db.Questions.ExecuteUpdateAsync(s => s.SetProperty(q => q.ChapterId, (int?)null), ct);
            var days = await db.PlanDays.Include(d => d.Chapters).ToListAsync(ct);
            foreach (var d in days) d.Chapters.Clear();
            await db.SaveChangesAsync(ct);
            await db.Chapters.ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, (int?)null), ct);
            await db.Chapters.ExecuteDeleteAsync(ct);
        }

        var byCode = await db.Chapters.ToDictionaryAsync(c => c.Code, ct);
        int created = 0, updated = 0, skipped = 0, order = byCode.Count;
        var errors = new List<string>();
        foreach (var dto in items)
        {
            if (string.IsNullOrWhiteSpace(dto.Code) || dto.StartPage < 1) { skipped++; errors.Add($"Chapitre ignoré : « {dto.Title} »."); continue; }
            if (!byCode.TryGetValue(dto.Code, out var chapter))
            {
                chapter = new Chapter { Code = dto.Code, SortOrder = ++order };
                db.Chapters.Add(chapter);
                byCode[dto.Code] = chapter;
                created++;
            }
            else updated++;
            chapter.Title = dto.Title.Length > 400 ? dto.Title[..400] : dto.Title;
            chapter.StartPage = dto.StartPage;
            chapter.EndPage = Math.Max(dto.StartPage, dto.EndPage);
            chapter.Parent = dto.Parent is not null && byCode.TryGetValue(dto.Parent, out var parent) ? parent : null;
        }
        await db.SaveChangesAsync(ct);
        return new ImportResult(created, updated, skipped, errors);
    }

    /// <summary>Lit les signets du PDF pour proposer la liste des chapitres (codes déduits des titres « 2.3 … »).</summary>
    public static List<ChapterImport> ExtractChapters(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        var pageCount = document.NumberOfPages;
        var flat = new List<(ChapterImport Chapter, int Level)>();
        if (!document.TryGetBookmarks(out var bookmarks)) return [];

        var counters = new List<int>();
        void Walk(IEnumerable<BookmarkNode> nodes, int level, string? parentCode)
        {
            foreach (var node in nodes)
            {
                if (node is not DocumentBookmarkNode docNode) { Walk(node.Children, level, parentCode); continue; }
                while (counters.Count <= level) counters.Add(0);
                counters[level]++;
                for (var i = level + 1; i < counters.Count; i++) counters[i] = 0;
                var title = node.Title.Trim();
                var match = LeadingNumber().Match(title);
                var code = match.Success ? match.Groups[1].Value.TrimEnd('.') : "S" + string.Join('.', counters.Take(level + 1));
                if (flat.Any(f => f.Chapter.Code == code)) code = $"{code}~{flat.Count}";
                flat.Add((new ChapterImport { Code = code, Title = title, StartPage = docNode.PageNumber, Parent = parentCode }, level));
                Walk(node.Children, level + 1, code);
            }
        }
        Walk(bookmarks.Roots, 0, null);

        // Fin = début du prochain signet de même niveau ou supérieur, moins une page.
        for (var i = 0; i < flat.Count; i++)
        {
            var (chapter, level) = flat[i];
            var next = flat.Skip(i + 1).FirstOrDefault(f => f.Level <= level);
            chapter.EndPage = next.Chapter is null ? pageCount : Math.Max(chapter.StartPage, next.Chapter.StartPage - 1);
        }
        return flat.Select(f => f.Chapter).ToList();
    }

    public static int CountPages(string pdfPath)
    {
        using var document = PdfDocument.Open(pdfPath);
        return document.NumberOfPages;
    }

    [GeneratedRegex(@"^((?:\d+|[A-Z])(?:\.\d+)*\.?)\s")]
    private static partial Regex LeadingNumber();
}

public record QuestionBankStats(int Total, Dictionary<string, int> ByDomain, Dictionary<string, int> ByApproach, Dictionary<string, int> BySource);
