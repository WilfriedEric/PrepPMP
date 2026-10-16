using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model;
using PmpStudy.Web.Model.Entities;
using PmpStudy.Web.Model.Rules;
using PmpStudy.Web.Services;

namespace PmpStudy.Tests;

/// <summary>Base SQLite en mémoire, migrée et alimentée comme en production, avec une horloge contrôlable.</summary>
public sealed class TestDb : IDbContextFactory<AppDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly DbContextOptions<SqliteAppDbContext> options;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero));
    public StudyClock Clock { get; }

    private TestDb()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(connection).UseApplicationServiceProvider(IdentitySchema.Services).Options;
        Clock = new StudyClock(Time, TimeZoneInfo.Utc);
    }

    public static async Task<TestDb> CreateAsync(bool seedQuestions = true)
    {
        var db = new TestDb();
        await new DataSeeder(db).SeedAsync(); // migrations + tâches ECO, comme au démarrage de l'app
        if (seedQuestions)
        {
            var result = await db.Content.ImportQuestionsAsync(SyntheticBank());
            if (!result.Ok) throw new InvalidOperationException(string.Join("; ", result.Errors));
        }
        return db;
    }

    /// <summary>Banque synthétique : 3 questions par tâche ECO (78), approches variées, quelques choix multiples.</summary>
    public static List<QuestionImport> SyntheticBank()
    {
        var approaches = new[] { "predictive", "agile", "hybrid" };
        var list = new List<QuestionImport>();
        foreach (var task in EcoCatalog.Tasks)
            for (var i = 0; i < 3; i++)
            {
                var multiple = i == 2 && task.Code.EndsWith('1');
                list.Add(new QuestionImport
                {
                    Id = $"t-{task.Code}-{i}",
                    Type = multiple ? "multiple" : "single",
                    EcoTask = task.Code,
                    Approach = approaches[i],
                    Stem = $"Question {i + 1} sur {task.Title} ?",
                    Options =
                    [
                        new() { Text = "Bonne réponse", Correct = true },
                        new() { Text = "Distracteur 1", Correct = multiple },
                        new() { Text = "Distracteur 2" },
                        new() { Text = "Distracteur 3" },
                    ],
                    Explanation = "Explication de test.",
                    Source = "Banque de test",
                });
            }
        return list;
    }

    public AppDbContext CreateDbContext() => new SqliteAppDbContext(options);

    public ContentService Content => new(this);
    public SessionService Sessions => new(this, Clock);
    public ErrorLogService Errors => new(this, Clock);
    public StudyService Study => new(this, Clock);
    public NoteService Notes => new(this, Clock);
    public ReportService Reports => new(this, Clock, Study);
    public FakePushSender Push { get; } = new();
    public NotificationService Notifications => new(this, Clock, Push, Study, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}

public class FakePushSender : IPushSender
{
    public List<(string Endpoint, string Payload)> Sent { get; } = [];
    public HashSet<string> GoneEndpoints { get; } = [];

    public Task<PushResult> SendAsync(PmpStudy.Web.Model.Entities.PushDevice device, string payloadJson, VapidKeys keys, CancellationToken ct)
    {
        if (GoneEndpoints.Contains(device.Endpoint)) return Task.FromResult(PushResult.Gone);
        Sent.Add((device.Endpoint, payloadJson));
        return Task.FromResult(PushResult.Sent);
    }
}
