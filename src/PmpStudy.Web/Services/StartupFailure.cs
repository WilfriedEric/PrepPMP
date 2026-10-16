using System.Net;
using System.Text;

namespace PmpStudy.Web.Services;

/// <summary>
/// Page de diagnostic servie quand l'application ne peut pas démarrer. Sur un hébergement mutualisé, les journaux
/// du serveur sont difficiles d'accès : la cause s'affiche directement dans le navigateur. Aucun secret n'est
/// affiché (la chaîne de connexion n'apparaît jamais dans les messages de SQL Server).
/// </summary>
public static class StartupFailure
{
    public static async Task RunAsync(string[] args, Exception error)
    {
        Console.Error.WriteLine("PMP Study : échec du démarrage.");
        Console.Error.WriteLine(error);

        var builder = WebApplication.CreateSlimBuilder(args);
        var app = builder.Build();
        var page = Render(error, app.Environment.EnvironmentName);
        app.Run(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(page);
        });
        await app.RunAsync();
    }

    private static string Render(Exception error, string environment)
    {
        var messages = new StringBuilder();
        for (var e = error; e is not null; e = e.InnerException)
            messages.Append("<li><code>").Append(WebUtility.HtmlEncode(e.GetType().Name)).Append("</code> ")
                .Append(WebUtility.HtmlEncode(e.Message)).Append("</li>");

        return $$"""
            <!doctype html>
            <html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>PMP Study ne démarre pas</title>
            <style>
              :root { color-scheme: light dark; }
              body { margin: 0; font: 15px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; background: #f6f6f7; color: #18181b; padding: 32px 16px; }
              @media (prefers-color-scheme: dark) { body { background: #0b0b0d; color: #f4f4f5; } .card { background: #141417 !important; border-color: #27272a !important; } }
              main { max-width: 760px; margin: 0 auto; }
              .card { background: #fff; border: 1px solid #e4e4e7; border-radius: 14px; padding: 20px 24px; margin-top: 16px; }
              h1 { font-size: 26px; letter-spacing: -0.02em; margin: 0 0 6px; }
              code { font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 13px; }
              li { margin: 6px 0; }
              .err li { color: #b91c1c; }
              @media (prefers-color-scheme: dark) { .err li { color: #f87171; } }
            </style></head>
            <body><main>
              <h1>PMP Study ne démarre pas</h1>
              <p>L'application est installée mais n'a pas pu s'initialiser (environnement : <code>{{WebUtility.HtmlEncode(environment)}}</code>). Cause :</p>
              <div class="card"><ul class="err">{{messages}}</ul></div>
              <div class="card">
                <strong>Causes fréquentes</strong>
                <ul>
                  <li><b>Chaîne de connexion absente</b> : le fichier <code>appsettings.Production.json</code> doit être à côté de <code>PmpStudy.Web.exe</code> (voir <code>deploy/appsettings.Production.exemple.json</code>).</li>
                  <li><b>Serveur ou identifiants SQL incorrects</b> (« Login failed », « network-related error ») : reprendre le nom du serveur, de la base, l'utilisateur et le mot de passe exactement comme dans le panneau d'hébergement, et garder <code>TrustServerCertificate=True</code>.</li>
                  <li><b>« There is already an object named… »</b> : la base contient des tables d'un essai précédent. Supprimer puis recréer la base vide dans le panneau ; l'application crée les tables au démarrage.</li>
                  <li><b>« permission denied » / « CREATE TABLE »</b> : l'utilisateur SQL doit être propriétaire de la base (<code>db_owner</code>), ou exécuter <code>deploy/creation-base-sqlserver.sql</code> une fois.</li>
                </ul>
                <p>Après correction, redémarrer l'application : déposer puis supprimer un fichier <code>app_offline.htm</code> à la racine du site, ou « Redémarrer » dans Plesk.</p>
              </div>
            </main></body></html>
            """;
    }
}
