using System.Text;
using Microsoft.AspNetCore.Mvc;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Infrastructure.Services;

namespace PmpStudy.Web.Api;

public record CreateSessionRequest(
    string Type,
    int? Count,
    int? SecondsPerQuestion,
    string? Feedback,
    Domain? Domain,
    List<string>? EcoTasks,
    Approach? Approach,
    int? ChapterId,
    string? Source,
    bool? DueOnly);

public record AnswerRequest(int ItemId, List<int> OptionIds, int? ElapsedSeconds, bool? Flagged, bool? Doubt);
public record BreakRequest(string Action);
public record NoteRequest(int? Page, string Content, string? Tags, int? ChapterId, int? EcoTaskId);
public record PushKeys(string P256dh, string Auth);
public record PushSubscribeRequest(string Endpoint, PushKeys Keys);
public record PushUnsubscribeRequest(string Endpoint);

/// <summary>Petite API REST du brief : sert aussi aux imports et à l'export du bilan. Tout est authentifié.</summary>
public static class StudyApi
{
    public static IEndpointRouteBuilder MapStudyApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().DisableAntiforgery();

        api.MapGet("/plan/today", async (StudyService study) => Results.Ok(ToDto(await study.TodayAsync())));

        api.MapGet("/course/pdf", async (IPdfStore store, HttpContext http) =>
        {
            var stream = await store.OpenReadAsync(http.RequestAborted);
            if (stream is null) return Results.NotFound("Aucun PDF chargé : passe par /admin/import.");
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.Stream(stream, "application/pdf", enableRangeProcessing: true);
        });

        // ----- Notes -----
        api.MapGet("/notes", async (NoteService notes, int? page, int? chapterId, int? ecoTaskId, string? tag, string? q) =>
            Results.Ok((await notes.ListAsync(new NoteQuery { Page = page, ChapterId = chapterId, EcoTaskId = ecoTaskId, Tag = tag, Text = q }))
                .Select(ToDto)));
        api.MapGet("/notes/{id:int}", async (NoteService notes, int id) =>
            await notes.GetAsync(id) is { } n ? Results.Ok(ToDto(n)) : Results.NotFound());
        api.MapPost("/notes", async (NoteService notes, NoteRequest r) =>
        {
            var saved = await notes.SaveAsync(new Note { Page = r.Page, Content = r.Content, Tags = r.Tags ?? "", ChapterId = r.ChapterId, EcoTaskId = r.EcoTaskId });
            return Results.Created($"/api/notes/{saved.Id}", new { saved.Id });
        });
        api.MapPut("/notes/{id:int}", async (NoteService notes, int id, NoteRequest r) =>
        {
            if (await notes.GetAsync(id) is null) return Results.NotFound();
            await notes.SaveAsync(new Note { Id = id, Page = r.Page, Content = r.Content, Tags = r.Tags ?? "", ChapterId = r.ChapterId, EcoTaskId = r.EcoTaskId });
            return Results.NoContent();
        });
        api.MapDelete("/notes/{id:int}", async (NoteService notes, int id) =>
            await notes.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/notes/export.md", async (NoteService notes) =>
            Results.File(Encoding.UTF8.GetBytes(NoteService.ToMarkdown(await notes.ListAsync(new NoteQuery()))), "text/markdown; charset=utf-8", "notes-pmp.md"));

        // ----- Sessions -----
        api.MapPost("/sessions", async (SessionService sessions, CreateSessionRequest r) =>
        {
            try
            {
                int id;
                switch (r.Type.ToLowerInvariant())
                {
                    case "exam":
                    case "examen":
                        id = (await sessions.CreateExamAsync(r.Count ?? 180)).SessionId;
                        break;
                    case "errors":
                    case "replay":
                        id = await sessions.CreateErrorReplayAsync(r.DueOnly ?? true, r.Count ?? 30);
                        break;
                    default:
                        id = await sessions.CreateTrainingAsync(new TrainingRequest
                        {
                            Count = r.Count ?? 20,
                            SecondsPerQuestion = r.SecondsPerQuestion ?? 80,
                            FeedbackMode = r.Feedback?.ToLowerInvariant() is "end" or "fin" ? FeedbackMode.AtEnd : FeedbackMode.Immediate,
                            Domain = r.Domain,
                            EcoTasks = r.EcoTasks ?? [],
                            Approach = r.Approach,
                            ChapterId = r.ChapterId,
                            Source = r.Source,
                        });
                        break;
                }
                return Results.Created($"/api/sessions/{id}", new { id });
            }
            catch (SessionException ex) { return Results.BadRequest(ex.Message); }
        });

        api.MapGet("/sessions/{id:int}", async (SessionService sessions, IStudyClock clock, int id) =>
            await sessions.GetAsync(id) is { } s ? Results.Ok(ToDto(s, clock)) : Results.NotFound());

        api.MapPost("/sessions/{id:int}/answers", async (SessionService sessions, int id, AnswerRequest r) =>
        {
            var result = await sessions.AnswerAsync(id, r.ItemId, r.OptionIds, r.ElapsedSeconds ?? 0);
            if (result.Accepted && (r.Flagged is not null || r.Doubt is not null))
                await sessions.SetMarksAsync(id, r.ItemId, r.Flagged ?? false, r.Doubt ?? false);
            return result.Accepted ? Results.Ok(result) : Results.BadRequest(result);
        });

        api.MapPost("/sessions/{id:int}/sections/{n:int}/break", async (SessionService sessions, int id, int n, BreakRequest r) =>
        {
            var error = r.Action.ToLowerInvariant() switch
            {
                "start" => await sessions.StartBreakAsync(id, n),
                "end" => await sessions.StartNextSectionAsync(id),
                _ => "Action attendue : start ou end.",
            };
            return error is null ? Results.NoContent() : Results.BadRequest(error);
        });

        api.MapPost("/sessions/{id:int}/sections/{n:int}/submit", async (SessionService sessions, int id, int n) =>
            await sessions.SubmitSectionAsync(id, n) is { } error ? Results.BadRequest(error) : Results.NoContent());

        api.MapPost("/sessions/{id:int}/finish", async (SessionService sessions, IStudyClock clock, int id) =>
            await sessions.FinishAsync(id) is { } s ? Results.Ok(ToDto(s, clock)) : Results.NotFound());

        // ----- Carnet d'erreurs -----
        api.MapGet("/errors", async (ErrorLogService errors, string? due, string? filter) =>
        {
            var f = due == "today" ? ErrorFilter.Due : Enum.TryParse<ErrorFilter>(filter, true, out var parsed) ? parsed : ErrorFilter.All;
            return Results.Ok((await errors.ListAsync(f)).Select(e => new
            {
                e.Id, e.QuestionId, ecoTask = e.EcoTask?.Code, stem = e.Question?.Stem ?? e.Summary, e.Why, e.Trap, e.MindsetReflex,
                status = e.Status.ToString(), origin = e.Origin.ToString(), e.NextReview, e.ReviewCount, e.TimesWrong, incomplete = e.IsIncomplete,
            }));
        });

        // ----- Imports et exports -----
        api.MapPost("/questions/import", async (ContentService content, HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            try { return Results.Ok(await content.ImportQuestionsAsync(ContentService.ParseQuestions(json))); }
            catch (System.Text.Json.JsonException ex) { return Results.BadRequest($"JSON invalide : {ex.Message}"); }
        });
        api.MapGet("/questions/export", async (ContentService content) =>
            Results.Json(await content.ExportQuestionsAsync(), ContentService.Json));
        api.MapPost("/plan/import", async (ContentService content, List<PlanDayImport> days) =>
            Results.Ok(await content.ImportPlanAsync(days)));

        api.MapGet("/reports/weekly/{semaine}", async (ReportService reports, string semaine) =>
        {
            var raw = semaine.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? semaine[..^3] : semaine;
            int week;
            if (raw is "current" or "courante") week = await reports.CurrentWeekAsync();
            else if (!int.TryParse(raw, out week)) return Results.BadRequest("Semaine attendue : un numéro, ou « current ».");
            var md = await reports.WeeklyMarkdownAsync(week);
            return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown; charset=utf-8", $"bilan-semaine-{week}.md");
        });

        // ----- Notifications (PWA) -----
        api.MapGet("/push/public-key", async (NotificationService push) => Results.Ok(new { publicKey = (await push.GetVapidKeysAsync()).PublicKey }));
        api.MapPost("/push/subscribe", async (NotificationService push, PushSubscribeRequest r, HttpRequest http) =>
        {
            try { await push.SubscribeAsync(r.Endpoint, r.Keys.P256dh, r.Keys.Auth, http.Headers.UserAgent.ToString()); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
            return Results.NoContent();
        });
        api.MapPost("/push/unsubscribe", async (NotificationService push, PushUnsubscribeRequest r) =>
        {
            await push.UnsubscribeAsync(r.Endpoint);
            return Results.NoContent();
        });
        api.MapPost("/push/test", async (NotificationService push) =>
        {
            var message = await push.BuildTodayMessageAsync() ?? new Core.Rules.ReminderMessage("PMP Study", "Tout est fait pour aujourd'hui. Bravo !", "/");
            var (sent, removed, failed) = await push.SendToAllAsync(message);
            return Results.Ok(new { sent, removed, failed });
        });

        api.MapGet("/export", async (ReportService reports, ContentService content, IStudyClock clock) =>
            Results.File(Encoding.UTF8.GetBytes(await reports.FullExportJsonAsync(content)), "application/json", $"pmp-study-export-{clock.Today:yyyy-MM-dd}.json"));

        return app;
    }

    private static object ToDto(Note n) => new
    {
        n.Id, n.Page, n.Content, n.Tags, n.ChapterId, chapter = n.Chapter?.Code, n.EcoTaskId, ecoTask = n.EcoTask?.Code, n.CreatedUtc, n.UpdatedUtc,
    };

    private static object ToDto(TodaySummary t) => new
    {
        t.Date,
        plan = t.PlanDay is null ? null : new
        {
            t.PlanDay.Date, t.PlanDay.Week, t.PlanDay.PageFrom, t.PlanDay.PageTo, t.PlanDay.QuestionGoal, t.PlanDay.Label,
            status = t.PlanDay.Status.ToString(),
            chapters = t.PlanDay.Chapters.Select(c => new { c.Code, c.Title, c.StartPage }),
            ecoTasks = t.PlanDay.EcoTasks.Select(e => e.Code),
        },
        week = t.Week.Select(d => new { d.Date, status = d.Status.ToString() }),
        t.ExamDate, t.DaysToExam, t.NextSimulation, t.QuestionsAnsweredToday, t.ReadingMinutesToday,
        t.PagesReadToday, t.PagesDone, t.QuestionsDone, t.ErrorsDue, t.ErrorsIncomplete, t.LastPage,
        openSessionId = t.OpenSession?.Id,
    };

    private static object ToDto(StudySession s, IStudyClock clock)
    {
        var reveal = s.IsFinished || s.FeedbackMode == FeedbackMode.Immediate;
        return new
        {
            s.Id, type = s.Type.ToString(), s.Title, s.StartUtc, s.EndUtc,
            deadlineUtc = Core.Rules.SessionClock.EffectiveDeadline(s, clock.UtcNow),
            remainingSeconds = (int)Core.Rules.SessionClock.Remaining(s, clock.UtcNow).TotalSeconds,
            s.ScorePercent, s.CorrectCount,
            sections = s.Sections.OrderBy(x => x.Index).Select(x => new { x.Index, x.StartUtc, x.EndUtc, x.BreakStartUtc, x.BreakEndUtc }),
            items = s.Items.OrderBy(i => i.Order).Select(i => new
            {
                i.Id, i.Order, i.SectionIndex, i.QuestionId, stem = i.Question?.Stem, type = i.Question?.Type.ToString(),
                options = i.Question?.Options.OrderBy(o => o.Order).Select(o => new { o.Id, o.Text, correct = reveal && (s.IsFinished || i.AnsweredUtc != null) ? o.IsCorrect : (bool?)null }),
                answer = Core.Rules.Scoring.ParseAnswer(i.AnswerJson), i.Flagged, i.Doubt, i.TimeSeconds,
                isCorrect = s.IsFinished || (s.FeedbackMode == FeedbackMode.Immediate && i.AnsweredUtc != null) ? i.IsCorrect : null,
                explanation = s.IsFinished || (s.FeedbackMode == FeedbackMode.Immediate && i.AnsweredUtc != null) ? i.Question?.Explanation : null,
            }),
        };
    }
}
