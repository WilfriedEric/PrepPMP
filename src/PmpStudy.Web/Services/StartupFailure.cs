using System.Net;
using System.Text;

namespace PmpStudy.Web.Services;

/// <summary>État de la préparation de la base (migrations, données de référence), faite en arrière-plan.</summary>
public class StartupState
{
    private volatile bool ready;
    public bool IsReady => ready;
    public Exception? Error { get; private set; }
    public int Attempts { get; private set; }
    public DateTime? NextAttemptUtc { get; private set; }

    public void Attempt() => Attempts++;
    public void Ready() { Error = null; NextAttemptUtc = null; ready = true; }
    public void Fail(Exception error, DateTime nextAttemptUtc) { Error = error; NextAttemptUtc = nextAttemptUtc; }
}

/// <summary>
/// Prépare la base sans bloquer le démarrage du site : sous IIS, un démarrage de plus de 2 minutes est coupé
/// (erreur 500.37), et une exception au démarrage donne l'erreur 500.30 sans explication. En cas d'échec,
/// nouvel essai toutes les 30 secondes ; la cause est affichée en attendant.
/// </summary>
public class DatabaseInitializer(IServiceScopeFactory scopes, StartupState state, ILogger<DatabaseInitializer> logger) : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            state.Attempt();
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(stoppingToken);
                state.Ready();
                logger.LogInformation("Base de données prête");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Préparation de la base en échec (essai {Attempt})", state.Attempts);
                state.Fail(ex, DateTime.UtcNow + RetryDelay);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }
}

public static class StartupFailure
{
    // Fichiers du mode hors ligne et ressources statiques : servis même si la base n'est pas prête.
    private static readonly string[] AlwaysServed = ["/hors-ligne", "/fonts", "/icons", "/lib", "/sw.js", "/manifest.webmanifest", "/favicon"];

    /// <summary>Tant que la base n'est pas prête : page d'attente, ou page de diagnostic si sa préparation échoue.</summary>
    public static IApplicationBuilder UseStartupGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var state = ctx.RequestServices.GetRequiredService<StartupState>();
        if (state.IsReady || AlwaysServed.Any(p => ctx.Request.Path.StartsWithSegments(p) || ctx.Request.Path.Value?.StartsWith(p) == true))
        {
            await next();
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/health"))
        {
            // Le service de surveillance garde le site éveillé même pendant un incident.
            await ctx.Response.WriteAsync(state.Error is null ? "starting" : "database-error");
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.RetryAfter = "5";
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            await ctx.Response.WriteAsync("Base de données indisponible.");
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName;
        await ctx.Response.WriteAsync(state.Error is null
            ? Waiting(state.Attempts)
            : Render(state.Error, env, $"Nouvel essai automatique toutes les {DatabaseInitializer.RetryDelay.TotalSeconds:0} secondes (essai n°{state.Attempts}) : la page se recharge seule.", refreshSeconds: 15));
    });

    /// <summary>Dernier recours si l'application ne peut même pas être construite (configuration invalide).</summary>
    public static async Task RunAsync(string[] args, Exception error)
    {
        Console.Error.WriteLine("PMP Study : échec du démarrage.");
        Console.Error.WriteLine(error);

        // Builder complet (pas « slim ») : il inclut l'intégration IIS, sans laquelle le site ne répond pas sous IIS.
        var app = WebApplication.CreateBuilder(args).Build();
        var page = Render(error, app.Environment.EnvironmentName, "Après correction, redémarrer le site.", refreshSeconds: null);
        app.Run(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/health")) { await ctx.Response.WriteAsync("configuration-error"); return; }
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(page);
        });
        await app.RunAsync();
    }

    private static string Waiting(int attempt) => Page("Démarrage…", $"""
        <h1>Démarrage de PMP Study…</h1>
        <p>Connexion à la base de données{(attempt > 1 ? $" (essai n°{attempt})" : "")}. La page se recharge seule.</p>
        """, refreshSeconds: 3);

    public static string Render(Exception error, string environment, string retryNote, int? refreshSeconds)
    {
        var messages = new StringBuilder();
        for (var e = error; e is not null; e = e.InnerException)
            messages.Append("<li><code>").Append(WebUtility.HtmlEncode(e.GetType().Name)).Append("</code> ")
                .Append(WebUtility.HtmlEncode(e.Message)).Append("</li>");

        return Page("PMP Study ne démarre pas", $$"""
            <h1>PMP Study ne démarre pas</h1>
            <p>L'application est installée mais n'a pas pu préparer la base de données (environnement : <code>{{WebUtility.HtmlEncode(environment)}}</code>). Cause :</p>
            <div class="card"><ul class="err">{{messages}}</ul></div>
            <div class="card">
              <strong>Causes fréquentes</strong>
              <ul>
                <li><b>Chaîne de connexion absente</b> : le fichier <code>appsettings.Production.json</code> doit être à côté de <code>PmpStudy.Web.exe</code> (voir <code>deploy/appsettings.Production.exemple.json</code>).</li>
                <li><b>« network-related » / « timeout »</b> : le serveur web ne joint pas SQL Server avec ce nom. Le nom utilisé dans SSMS depuis ton ordinateur n'est pas toujours celui à utiliser depuis le serveur web : essayer le nom indiqué par le panneau pour les applications (souvent <code>localhost</code>, <code>.\SQLEXPRESS</code> ou l'adresse IP interne), avec le port si besoin (<code>serveur,1433</code>).</li>
                <li><b>« Login failed »</b> : utilisateur ou mot de passe refusé. Garder <code>TrustServerCertificate=True</code>.</li>
                <li><b>« There is already an object named… »</b> : la base contient des tables d'un essai précédent. Supprimer puis recréer la base vide ; l'application crée les tables.</li>
                <li><b>« permission denied » / « CREATE TABLE »</b> : l'utilisateur SQL doit être propriétaire de la base (<code>db_owner</code>), ou exécuter <code>deploy/creation-base-sqlserver.sql</code> une fois.</li>
              </ul>
              <p>{{WebUtility.HtmlEncode(retryNote)}}</p>
            </div>
            """, refreshSeconds);
    }

    private static string Page(string title, string body, int? refreshSeconds) => $$"""
        <!doctype html>
        <html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        {{(refreshSeconds is { } r ? $"<meta http-equiv=\"refresh\" content=\"{r}\">" : "")}}
        <title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
          :root { color-scheme: light dark; }
          body { margin: 0; font: 15px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; background: #f6f6f7; color: #18181b; padding: 32px 16px; }
          main { max-width: 760px; margin: 0 auto; }
          .card { background: #fff; border: 1px solid #e4e4e7; border-radius: 14px; padding: 20px 24px; margin-top: 16px; }
          h1 { font-size: 26px; letter-spacing: -0.02em; margin: 0 0 6px; }
          code { font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 13px; }
          li { margin: 6px 0; }
          .err li { color: #b91c1c; }
          @media (prefers-color-scheme: dark) {
            body { background: #0b0b0d; color: #f4f4f5; }
            .card { background: #141417; border-color: #27272a; }
            .err li { color: #f87171; }
          }
        </style></head>
        <body><main>{{body}}</main></body></html>
        """;
}
