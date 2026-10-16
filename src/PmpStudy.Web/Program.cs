using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using PmpStudy.Web.Api;
using PmpStudy.Web.Components.Account;
using PmpStudy.Web.Components;
using PmpStudy.Web.Data;
using PmpStudy.Web.Services;

try
{
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Imports collés dans les champs texte (plusieurs centaines de questions) : messages plus gros que les 32 Ko par défaut.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 8 * 1024 * 1024);
builder.Services.AddMudServices();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.AddAuthorization();

builder.Services.AddPmpStudy(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Compte unique, identifiant et mot de passe : pas de confirmation par e-mail ni de double authentification.
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 10;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
    // L'API répond 401/403 au lieu de rediriger vers la page de connexion.
    options.Events.OnRedirectToLogin = ctx => ApiAwareRedirect(ctx, StatusCodes.Status401Unauthorized);
    options.Events.OnRedirectToAccessDenied = ctx => ApiAwareRedirect(ctx, StatusCodes.Status403Forbidden);
});


if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddApplicationInsightsTelemetry();

// Upload du PDF du cours depuis /admin/import.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 200 * 1024 * 1024);

var app = builder.Build();

// Tant que la base n'est pas prête : page d'attente ou de diagnostic (le mode hors ligne reste servi).
app.UseStartupGate();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapStudyApi();
// Sonde publique pour un service de surveillance (garde l'app réveillée sur un hébergement mutualisé).
app.MapGet("/health", () => Results.Text("ok")).AllowAnonymous();
// Application hors ligne (fichiers statiques, mise en cache par le service worker).
app.MapGet("/hors-ligne", () => Results.Redirect("/hors-ligne/index.html")).AllowAnonymous();

app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // Démarrage impossible (base injoignable, chaîne de connexion absente…) : sur IIS, au lieu de l'erreur
    // générique 500.30, l'application démarre en mode dégradé et affiche la cause.
    await StartupFailure.RunAsync(args, ex);
}

static Task ApiAwareRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int status)
{
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }
    ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

public partial class Program;
