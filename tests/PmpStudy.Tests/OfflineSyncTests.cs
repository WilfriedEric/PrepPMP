using Microsoft.EntityFrameworkCore;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Services;

namespace PmpStudy.Tests;

public class OfflineSyncTests
{
    private static SyncPush Push(params SyncSession[] sessions) => new() { DeviceId = "device-1", DeviceName = "Téléphone", Sessions = [.. sessions] };

    private static SyncSession Session(string clientId, DateTime end, params (int QuestionId, int[] Answer)[] answers) => new()
    {
        ClientId = clientId,
        Type = "training",
        Title = "Entraînement · 2 questions",
        StartUtc = end.AddMinutes(-3),
        EndUtc = end,
        DurationSeconds = 160,
        Items = answers.Select(a => new SyncItem { QuestionId = a.QuestionId, Answer = [.. a.Answer], AnsweredUtc = end.AddMinutes(-1), TimeSeconds = 40 }).ToList(),
    };

    [Fact]
    public async Task Pack_contains_the_bank_with_answers_the_error_log_and_records_the_device()
    {
        await using var db = await TestDb.CreateAsync();
        var pack = await db.Offline.BuildPackAsync("device-1", "Téléphone");

        Assert.Equal(78, pack.Questions.Count);
        var q = pack.Questions[0];
        Assert.Equal([1, 2, 3, 4], q.Options.Select(o => o.Order));
        Assert.True(q.Options[0].Correct);
        Assert.NotNull(q.Domain);
        Assert.Equal(26, pack.EcoTasks.Count);
        Assert.Equal("Africa/Abidjan", pack.TimeZone);

        var device = Assert.Single(await db.Offline.DevicesAsync());
        Assert.Equal("Téléphone", device.Name);
        Assert.NotNull(device.LastPullUtc);
    }

    [Fact]
    public async Task Offline_session_is_rescored_by_the_server_feeds_the_error_log_and_is_imported_once()
    {
        await using var db = await TestDb.CreateAsync();
        var pack = await db.Offline.BuildPackAsync();
        var (right, wrong) = (pack.Questions[0].Id, pack.Questions[3].Id);
        // Le client prétend avoir tout juste : le serveur recorrige d'après les numéros d'options.
        var push = Push(Session("s-1", db.Clock.UtcNow.AddHours(-2), (right, [1]), (wrong, [2])));

        var result = await db.Offline.ApplyAsync(push);
        Assert.Equal(1, result.SessionsImported);

        await using (var ctx = db.CreateDbContext())
        {
            var session = await ctx.Sessions.Include(s => s.Items).SingleAsync(s => s.ClientId == "s-1");
            Assert.True(session.IsFinished);
            Assert.Equal(1, session.CorrectCount);
            Assert.Equal(50, session.ScorePercent);
            Assert.EndsWith(OfflineSyncService.OfflineSuffix, session.Title);
            var error = await ctx.ErrorEntries.SingleAsync();
            Assert.Equal(wrong, error.QuestionId);
            Assert.Equal(DateOnly.FromDateTime(db.Clock.UtcNow).AddDays(1), error.NextReview);
        }

        // Envoi rejoué (réponse perdue côté appareil) : aucun doublon.
        var again = await db.Offline.ApplyAsync(push);
        Assert.Equal(0, again.SessionsImported);
        Assert.Equal(1, again.SessionsAlreadyKnown);
        await using var check = db.CreateDbContext();
        Assert.Equal(1, await check.Sessions.CountAsync());
        Assert.Equal(1, await check.ErrorEntries.CountAsync());
    }

    [Fact]
    public async Task Offline_note_is_created_once_then_updated_and_deleted_when_untouched_online()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var push = new SyncPush { DeviceId = "d", Notes = [new SyncNote { ClientId = "n-1", Page = 12, Content = "Hors ligne", Tags = "risque", UpdatedUtc = db.Clock.UtcNow }] };

        var created = await db.Offline.ApplyAsync(push);
        var mapping = Assert.Single(created.NoteIds);
        Assert.Equal(1, created.NotesCreated);
        Assert.Equal(0, (await db.Offline.ApplyAsync(push)).NotesCreated); // renvoi : rien de plus

        db.Time.Advance(TimeSpan.FromMinutes(5));
        var edit = new SyncNote { ClientId = "n-1", Id = mapping.Id, Page = 12, Content = "Modifiée", BaseUpdatedUtc = mapping.UpdatedUtc, UpdatedUtc = db.Clock.UtcNow };
        Assert.Equal(1, (await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Notes = [edit] })).NotesUpdated);
        var note = Assert.Single(await db.Notes.ListAsync(new NoteQuery()));
        Assert.Equal("Modifiée", note.Content);

        var delete = new SyncNote { ClientId = "n-1", Id = note.Id, Content = note.Content, Deleted = true, BaseUpdatedUtc = note.UpdatedUtc, UpdatedUtc = db.Clock.UtcNow };
        Assert.Equal(1, (await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Notes = [delete] })).NotesDeleted);
        Assert.Empty(await db.Notes.ListAsync(new NoteQuery()));
    }

    [Fact]
    public async Task Note_changed_online_and_offline_keeps_both_versions()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var online = await db.Notes.SaveAsync(new Note { Page = 3, Content = "Version d'origine" });
        var downloaded = online.UpdatedUtc;

        db.Time.Advance(TimeSpan.FromMinutes(10));
        await db.Notes.SaveAsync(new Note { Id = online.Id, Page = 3, Content = "Modifiée en ligne" });

        var offline = new SyncNote { ClientId = "server-" + online.Id, Id = online.Id, Page = 3, Content = "Modifiée hors ligne", BaseUpdatedUtc = downloaded, UpdatedUtc = db.Clock.UtcNow };
        var result = await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Notes = [offline] });

        Assert.Single(result.Conflicts);
        var notes = await db.Notes.ListAsync(new NoteQuery());
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, n => n.Content == "Modifiée en ligne");
        Assert.Contains(notes, n => n.Content == "Modifiée hors ligne" && n.Tags.Contains("conflit"));

        // Suppression hors ligne d'une note modifiée en ligne : la note est gardée.
        var delete = new SyncNote { ClientId = "x", Id = online.Id, Content = "Version d'origine", Deleted = true, BaseUpdatedUtc = downloaded, UpdatedUtc = db.Clock.UtcNow };
        var kept = await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Notes = [delete] });
        Assert.Equal(0, kept.NotesDeleted);
        Assert.Equal(2, (await db.Notes.ListAsync(new NoteQuery())).Count);
    }

    [Fact]
    public async Task Error_analysis_is_merged_field_by_field_and_reviews_apply_once()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var id = await db.Errors.AddExternalAsync("Question du PMI Study Hall", "Study Hall", null, "Lu trop vite", null, null);

        // En ligne, « pourquoi » change après le téléchargement ; hors ligne, « pourquoi » et « piège » changent.
        await db.Errors.SaveDetailsAsync(id, "Confondu avec le sponsor", null, null, null, "Question du PMI Study Hall");
        var edit = new SyncErrorEdit
        {
            Id = id, BaseWhy = "Lu trop vite", Why = "Pas lu la dernière phrase", Trap = "Le mot « d'abord »", UpdatedUtc = db.Clock.UtcNow,
        };
        var result = await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", ErrorEdits = [edit] });
        Assert.Single(result.Conflicts);

        var entry = (await db.Errors.ListAsync(ErrorFilter.All)).Single();
        Assert.Equal("Le mot « d'abord »", entry.Trap);                 // seul l'appareil l'a changé
        Assert.Contains("Confondu avec le sponsor", entry.Why);          // les deux versions sont gardées
        Assert.Contains("Pas lu la dernière phrase", entry.Why);
        Assert.Empty((await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", ErrorEdits = [edit] })).Conflicts); // renvoi : rien de plus

        var review = new SyncErrorReview { ClientId = "r-1", Id = id, KnewIt = true, AtUtc = db.Clock.UtcNow };
        await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", ErrorReviews = [review] });
        await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", ErrorReviews = [review] });
        entry = (await db.Errors.ListAsync(ErrorFilter.All)).Single();
        Assert.Equal(1, entry.ReviewCount);
        Assert.Equal(1, entry.ConsecutiveCorrect);
    }

    [Fact]
    public async Task Reading_time_is_upserted_and_last_page_saved()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var reading = new SyncReading { ClientId = "l-1", StartedUtc = db.Clock.UtcNow.AddMinutes(-30), UpdatedUtc = db.Clock.UtcNow, PageFrom = 40, PageTo = 45, Minutes = 12 };
        await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Readings = [reading], LastPage = 45 });
        reading.PageTo = 48;
        reading.Minutes = 20;
        await db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Readings = [reading] });

        await using var ctx = db.CreateDbContext();
        var log = await ctx.ReadingLogs.SingleAsync();
        Assert.Equal((40, 48, 20.0), (log.PageFrom, log.PageTo, log.Minutes));
        Assert.Equal("45", await db.Study.GetSettingAsync(SettingKeys.LastPage));
    }

    [Fact]
    public async Task Invalid_push_is_rejected()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        await Assert.ThrowsAsync<SyncException>(() => db.Offline.ApplyAsync(new SyncPush { DeviceId = "" }));
        await Assert.ThrowsAsync<SyncException>(() => db.Offline.ApplyAsync(new SyncPush { DeviceId = "d", Sessions = [new SyncSession { ClientId = "" }] }));
    }
}
