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
        var connection = config.GetConnectionString("DefaultConnection");
        var provider = DatabaseProvider.Resolve(config["Database:Provider"], connection);
        if (provider == DatabaseProvider.Sqlite)
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
            services.AddDbContextFactory<SqlServerAppDbContext>(o => o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));
            services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactory<SqlServerAppDbContext>>();
        }
        // Contexte « scoped » pour ASP.NET Core Identity et la persistance des clés de session.
        services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddDataProtection()
            .SetApplicationName("PmpStudy")
            .PersistKeysToDbContext<AppDbContext>();
        // Les clés sont lues à la première connexion, pas au démarrage : une base lente ou injoignable
        // ne doit pas empêcher le site de répondre (IIS coupe un démarrage de plus de 2 minutes).
        var eagerKeyRing = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "DataProtectionHostedService");
        if (eagerKeyRing is not null) services.Remove(eagerKeyRing);

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
        services.AddScoped<OfflineSyncService>();
        services.AddHttpClient(nameof(WebPushSender), c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IPushSender, WebPushSender>();
        // La base est préparée en arrière-plan : le site répond tout de suite (IIS coupe un démarrage de plus de 2 min).
        services.AddSingleton<StartupState>();
        services.AddHostedService<DatabaseInitializer>();
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
public class ExpiredSessionCloser(IServiceScopeFactory scopes, StartupState startup, ILogger<ExpiredSessionCloser> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!startup.IsReady) continue;
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
public class DataSeeder(IDbContextFactory<AppDbContext> dbFactory, ILogger<DataSeeder>? logger = null)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using (var probe = await dbFactory.CreateDbContextAsync(ct))
            await ReplaceObsoleteSchemaAsync(probe, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        var existing = await db.EcoTasks.Select(t => t.Code).ToListAsync(ct);
        foreach (var task in EcoCatalog.Tasks.Where(t => !existing.Contains(t.Code)))
            db.EcoTasks.Add(new() { Code = task.Code, Domain = task.Domain, Title = task.Title });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Les premières migrations ont été fusionnées en une seule : une base créée avant ne peut pas être mise à jour.
    /// En développement (SQLite), le fichier est mis de côté et recréé ; sur SQL Server, on s'arrête avec la marche à suivre.
    /// </summary>
    private async Task ReplaceObsoleteSchemaAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.Database.CanConnectAsync(ct)) return;
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var known = db.Database.GetMigrations().ToHashSet();
        var obsolete = applied.Where(m => !known.Contains(m)).ToList();
        if (obsolete.Count == 0 || applied.Any(known.Contains)) return;

        if (!db.Database.IsSqlite())
            throw new InvalidOperationException(
                "La base SQL Server a été créée par une ancienne version de PMP Study (migrations " + string.Join(", ", obsolete) +
                ") : supprime-la puis recrée-la vide dans le panneau d'hébergement, l'application créera les tables au démarrage.");

        var file = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        await db.Database.CloseConnectionAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var backup = $"{file}.ancien-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(file, backup);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(file + suffix)) File.Move(file + suffix, backup + suffix);
        logger?.LogWarning("Base SQLite créée par une ancienne version : mise de côté dans {Backup}, nouvelle base créée.", backup);
    }
}

/// <summary>
/// Choix du moteur : la forme de la chaîne de connexion l'emporte sur le réglage <c>Database:Provider</c>, pour qu'une
/// chaîne SQL Server ne soit jamais confiée au pilote SQLite (« keyword 'server' is not supported ») et inversement.
/// </summary>
public static class DatabaseProvider
{
    public const string SqlServer = "SqlServer";
    public const string Sqlite = "Sqlite";

    private static readonly string[] SqlServerKeys =
        ["server", "address", "addr", "network address", "initial catalog", "database", "user id", "uid", "trusted_connection", "integrated security", "trustservercertificate", "encrypt"];

    public static string Resolve(string? configured, string? connection)
    {
        var fallback = string.Equals(configured, Sqlite, StringComparison.OrdinalIgnoreCase) ? Sqlite : SqlServer;
        if (string.IsNullOrWhiteSpace(connection)) return fallback;
        var builder = new System.Data.Common.DbConnectionStringBuilder();
        try { builder.ConnectionString = connection; }
        catch (ArgumentException) { return fallback; }
        if (SqlServerKeys.Any(builder.ContainsKey)) return SqlServer;
        if (builder.TryGetValue("data source", out var source) || builder.TryGetValue("datasource", out source) || builder.TryGetValue("filename", out source))
        {
            var file = source?.ToString() ?? "";
            if (file.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                || file.Contains(":memory:", StringComparison.OrdinalIgnoreCase) || builder.ContainsKey("cache") || builder.ContainsKey("mode"))
                return Sqlite;
        }
        return fallback;
    }
}
