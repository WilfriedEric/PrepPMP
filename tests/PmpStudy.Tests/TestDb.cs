using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PmpStudy.Core.Entities;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure;
using PmpStudy.Infrastructure.Data;
using PmpStudy.Infrastructure.Services;

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
        if (seedQuestions)
            await new DataSeeder(db, db.Content, NullLogger<DataSeeder>.Instance).SeedAsync();
        else
        {
            await using var ctx = db.CreateDbContext();
            await ctx.Database.MigrateAsync();
            ctx.EcoTasks.AddRange(EcoCatalog.Tasks.Select(t => new EcoTask { Code = t.Code, Domain = t.Domain, Title = t.Title }));
            await ctx.SaveChangesAsync();
        }
        return db;
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

    public Task<PushResult> SendAsync(PmpStudy.Core.Entities.PushDevice device, string payloadJson, VapidKeys keys, CancellationToken ct)
    {
        if (GoneEndpoints.Contains(device.Endpoint)) return Task.FromResult(PushResult.Gone);
        Sent.Add((device.Endpoint, payloadJson));
        return Task.FromResult(PushResult.Sent);
    }
}
