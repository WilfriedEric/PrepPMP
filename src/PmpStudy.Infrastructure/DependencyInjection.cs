using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmpStudy.Core;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure.Data;
using PmpStudy.Infrastructure.Services;

namespace PmpStudy.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Configuration :
    /// <c>Database:Provider</c> = Sqlite (défaut) ou SqlServer, chaîne <c>ConnectionStrings:DefaultConnection</c> ;
    /// <c>Storage:Provider</c> = Local (défaut, <c>Storage:LocalPath</c>) ou Blob (<c>ConnectionStrings:Blob</c>, <c>Storage:Container</c>) ;
    /// <c>Study:TimeZone</c> = fuseau IANA de l'utilisateur.
    /// </summary>
    public static IServiceCollection AddPmpStudy(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var connection = config.GetConnectionString("DefaultConnection") ?? "DataSource=App_Data/pmpstudy.db;Cache=Shared";
        var provider = config["Database:Provider"] ?? "Sqlite";
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContextFactory<SqlServerAppDbContext>(o => o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure()));
            services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactory<SqlServerAppDbContext>>();
        }
        else
        {
            EnsureSqliteFolder(connection, contentRoot);
            services.AddDbContextFactory<SqliteAppDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactory<SqliteAppDbContext>>();
        }
        // Contexte « scoped » pour ASP.NET Core Identity.
        services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        var zoneId = config["Study:TimeZone"] ?? "Africa/Abidjan";
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var z) ? z : TimeZoneInfo.Utc;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IStudyClock>(sp => new StudyClock(sp.GetRequiredService<TimeProvider>(), zone));

        if ((config["Storage:Provider"] ?? "Local").Equals("Blob", StringComparison.OrdinalIgnoreCase))
        {
            var blobConnection = config.GetConnectionString("Blob") ?? throw new InvalidOperationException("ConnectionStrings:Blob manquante.");
            var container = new BlobContainerClient(blobConnection, config["Storage:Container"] ?? "course");
            services.AddSingleton<IPdfStore>(new BlobPdfStore(container, config["Storage:BlobName"] ?? "pmbok.pdf"));
        }
        else
        {
            var path = config["Storage:LocalPath"] ?? "App_Data/course/pmbok.pdf";
            services.AddSingleton<IPdfStore>(new LocalPdfStore(Path.IsPathRooted(path) ? path : Path.Combine(contentRoot, path)));
        }

        services.AddScoped<SessionService>();
        services.AddScoped<ErrorLogService>();
        services.AddScoped<ContentService>();
        services.AddScoped<StudyService>();
        services.AddScoped<NoteService>();
        services.AddScoped<ReportService>();
        services.AddScoped<DataSeeder>();
        services.AddScoped<NotificationService>();
        services.AddHttpClient(nameof(WebPushSender), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IPushSender, WebPushSender>();
        services.AddHostedService<ExpiredSessionCloser>();
        services.AddHostedService<DailyReminderService>();
        return services;
    }

    private static void EnsureSqliteFolder(string connection, string contentRoot)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection);
        if (string.IsNullOrEmpty(builder.DataSource) || builder.DataSource == ":memory:") return;
        var full = Path.IsPathRooted(builder.DataSource) ? builder.DataSource : Path.Combine(contentRoot, builder.DataSource);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    }
}

/// <summary>À l'échéance, le serveur clôture la session même si le navigateur est fermé.</summary>
public class ExpiredSessionCloser(IServiceScopeFactory scopes, ILogger<ExpiredSessionCloser> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var closed = await scope.ServiceProvider.GetRequiredService<SessionService>().CloseExpiredAsync(stoppingToken);
                if (closed > 0) logger.LogInformation("{Count} session(s) clôturée(s) à l'échéance", closed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Clôture des sessions expirées en échec");
            }
        }
    }
}

/// <summary>Applique les migrations, crée les tâches ECO et charge la banque de questions fournie avec l'app.</summary>
public class DataSeeder(IDbContextFactory<AppDbContext> dbFactory, ContentService content, ILogger<DataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
            var existing = await db.EcoTasks.Select(t => t.Code).ToListAsync(ct);
            foreach (var task in EcoCatalog.Tasks.Where(t => !existing.Contains(t.Code)))
                db.EcoTasks.Add(new() { Code = task.Code, Domain = task.Domain, Title = task.Title });
            await db.SaveChangesAsync(ct);
        }

        // Questions embarquées : on n'ajoute que celles qui manquent, sans écraser des corrections faites dans l'app.
        var assembly = typeof(DataSeeder).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(".Seed.") && n.EndsWith(".json")).Order())
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var items = ContentService.ParseQuestions(await reader.ReadToEndAsync(ct));
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = items.Select(i => i.Id).OfType<string>().ToList();
            var known = await db.Questions.Where(q => q.ExternalId != null && ids.Contains(q.ExternalId)).Select(q => q.ExternalId!).ToListAsync(ct);
            var missing = items.Where(i => i.Id is null || !known.Contains(i.Id)).ToList();
            if (missing.Count == 0) continue;
            var result = await content.ImportQuestionsAsync(missing, ct);
            logger.LogInformation("Banque {Name} : {Created} question(s) ajoutée(s), {Errors} avertissement(s)", name, result.Created, result.Errors.Count);
            foreach (var error in result.Errors) logger.LogWarning("{Error}", error);
        }
    }
}
