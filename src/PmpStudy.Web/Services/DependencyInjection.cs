using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model.Rules;
using PmpStudy.Web.Model;
using PmpStudy.Web.Services;

namespace PmpStudy.Web.Services;

public static class DependencyInjection
{
    /// <summary>
    /// Seule configuration hors base : <c>ConnectionStrings:DefaultConnection</c> et <c>Database:Provider</c>
    /// (SqlServer par défaut, Sqlite pour le développement local). Tout le reste (réglages, fuseau horaire,
    /// questions, PDF du cours, clés de notification et de session) est stocké dans la base.
    /// </summary>
    public static IServiceCollection AddPmpStudy(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var provider = config["Database:Provider"] ?? "SqlServer";
        var connection = config.GetConnectionString("DefaultConnection");
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            connection ??= "DataSource=App_Data/pmpstudy.db;Cache=Shared";
            EnsureSqliteFolder(connection, contentRoot);
            services.AddDbContextFactory<SqliteAppDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactory<SqliteAppDbContext>>();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("ConnectionStrings:DefaultConnection manquante : renseigne la chaîne de connexion SQL Server (voir README, hébergement).");
            services.AddDbContextFactory<SqlServerAppDbContext>(o => o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure()));
            services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactory<SqlServerAppDbContext>>();
        }
        // Contexte « scoped » pour ASP.NET Core Identity et la persistance des clés de session.
        services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddDataProtection()
            .SetApplicationName("PmpStudy")
            .PersistKeysToDbContext<AppDbContext>();

        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimeZoneSetting>();
        services.AddSingleton<IStudyClock>(sp =>
        {
            var zone = sp.GetRequiredService<TimeZoneSetting>();
            return new StudyClock(sp.GetRequiredService<TimeProvider>(), () => zone.Current);
        });
        services.AddSingleton<IPdfStore, DbPdfStore>();

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

/// <summary>Applique les migrations et crée les tâches ECO. Les questions s'importent depuis l'écran Import.</summary>
public class DataSeeder(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        var existing = await db.EcoTasks.Select(t => t.Code).ToListAsync(ct);
        foreach (var task in EcoCatalog.Tasks.Where(t => !existing.Contains(t.Code)))
            db.EcoTasks.Add(new() { Code = task.Code, Domain = task.Domain, Title = task.Title });
        await db.SaveChangesAsync(ct);
    }
}
