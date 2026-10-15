using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmpStudy.Core;
using PmpStudy.Core.Entities;
using PmpStudy.Core.Rules;
using PmpStudy.Infrastructure.Data;
using System.Security.Cryptography;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace PmpStudy.Infrastructure.Services;

public record VapidKeys(string PublicKey, string PrivateKey, string Subject);

public enum PushResult { Sent, Gone, Failed }

/// <summary>Envoi d'une notification Web Push à un appareil (remplaçable dans les tests).</summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(PushDevice device, string payloadJson, VapidKeys keys, CancellationToken ct);
}

/// <summary>Envoi Web Push chiffré en aes128gcm (RFC 8291), accepté par Chrome, Edge, Firefox et Safari (iOS 16.4+).</summary>
public class WebPushSender(IHttpClientFactory httpFactory, ILogger<WebPushSender> logger) : IPushSender
{
    public async Task<PushResult> SendAsync(PushDevice device, string payloadJson, VapidKeys keys, CancellationToken ct)
    {
        var client = new PushServiceClient(httpFactory.CreateClient(nameof(WebPushSender)))
        {
            DefaultAuthentication = new VapidAuthentication(keys.PublicKey, keys.PrivateKey) { Subject = keys.Subject },
        };
        var subscription = new PushSubscription
        {
            Endpoint = device.Endpoint,
            Keys = new Dictionary<string, string> { ["p256dh"] = device.P256dh, ["auth"] = device.Auth },
        };
        var message = new PushMessage(payloadJson)
        {
            Topic = "pmp-daily",
            Urgency = PushMessageUrgency.Normal,
            TimeToLive = (int)TimeSpan.FromHours(12).TotalSeconds,
        };
        try
        {
            await client.RequestPushMessageDeliveryAsync(subscription, message, ct);
            return PushResult.Sent;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return PushResult.Gone; // abonnement expiré ou révoqué
        }
        catch (Exception ex) when (ex is PushServiceClientException or HttpRequestException)
        {
            logger.LogWarning(ex, "Échec d'envoi de notification");
            return PushResult.Failed;
        }
    }

    /// <summary>Paire de clés VAPID (ECDSA P-256) encodée en base64url.</summary>
    public static (string PublicKey, string PrivateKey) GenerateVapidKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04; // point non compressé
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);
        return (Base64Url(publicKey), Base64Url(p.D!));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public record ReminderSettings(bool Enabled, TimeOnly Time, DateOnly? LastSent);

public class NotificationService(
    IDbContextFactory<AppDbContext> dbFactory,
    IStudyClock clock,
    IPushSender sender,
    StudyService study,
    IConfiguration config)
{
    // Charge utile lue par le service worker (JSON.parse), jamais insérée dans du HTML.
    private static readonly JsonSerializerOptions PayloadJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------- Clés VAPID ----------

    /// <summary>Clés de configuration (Push:VapidPublicKey / Push:VapidPrivateKey), sinon générées une fois et gardées en base.</summary>
    public async Task<VapidKeys> GetVapidKeysAsync(CancellationToken ct = default)
    {
        var subject = config["Push:Subject"] ?? "mailto:pmp-study@localhost";
        if (config["Push:VapidPublicKey"] is { Length: > 0 } pub && config["Push:VapidPrivateKey"] is { Length: > 0 } priv)
            return new VapidKeys(pub, priv, subject);

        var storedPub = await study.GetSettingAsync(SettingKeys.VapidPublicKey, ct);
        var storedPriv = await study.GetSettingAsync(SettingKeys.VapidPrivateKey, ct);
        if (storedPub is { Length: > 0 } && storedPriv is { Length: > 0 })
            return new VapidKeys(storedPub, storedPriv, subject);

        var generated = WebPushSender.GenerateVapidKeys();
        await study.SetSettingAsync(SettingKeys.VapidPublicKey, generated.PublicKey, ct);
        await study.SetSettingAsync(SettingKeys.VapidPrivateKey, generated.PrivateKey, ct);
        return new VapidKeys(generated.PublicKey, generated.PrivateKey, subject);
    }

    // ---------- Abonnements ----------

    public async Task SubscribeAsync(string endpoint, string p256dh, string auth, string? userAgent, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || endpoint.Length > 800)
            throw new ArgumentException("Point de terminaison de notification invalide.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var device = await db.PushDevices.FirstOrDefaultAsync(d => d.Endpoint == endpoint, ct);
        if (device is null)
        {
            device = new PushDevice { Endpoint = endpoint, CreatedUtc = clock.UtcNow };
            db.PushDevices.Add(device);
        }
        device.P256dh = p256dh;
        device.Auth = auth;
        device.UserAgent = userAgent is { Length: > 400 } ? userAgent[..400] : userAgent;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(string endpoint, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PushDevices.Where(d => d.Endpoint == endpoint).ExecuteDeleteAsync(ct);
    }

    public async Task<List<PushDevice>> DevicesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PushDevices.AsNoTracking().OrderBy(d => d.CreatedUtc).ToListAsync(ct);
    }

    public async Task<bool> IsSubscribedAsync(string endpoint, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PushDevices.AnyAsync(d => d.Endpoint == endpoint, ct);
    }

    // ---------- Réglages du rappel ----------

    public async Task<ReminderSettings> GetReminderAsync(CancellationToken ct = default)
    {
        var settings = await study.AllSettingsAsync(ct);
        var enabled = settings.GetValueOrDefault(SettingKeys.ReminderEnabled) == "true";
        var time = TimeOnly.TryParseExact(settings.GetValueOrDefault(SettingKeys.ReminderTime), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : DailyReminder.DefaultTime;
        DateOnly? last = DateOnly.TryParse(settings.GetValueOrDefault(SettingKeys.ReminderLastSent), CultureInfo.InvariantCulture, out var d) ? d : null;
        return new ReminderSettings(enabled, time, last);
    }

    public async Task SaveReminderAsync(bool enabled, TimeOnly time, CancellationToken ct = default)
    {
        var previous = await GetReminderAsync(ct);
        await study.SetSettingAsync(SettingKeys.ReminderEnabled, enabled ? "true" : "false", ct);
        await study.SetSettingAsync(SettingKeys.ReminderTime, time.ToString("HH:mm", CultureInfo.InvariantCulture), ct);
        // Nouvelle heure plus tard dans la journée : le rappel du jour peut repartir.
        if (previous.Time != time) await study.SetSettingAsync(SettingKeys.ReminderLastSent, null, ct);
    }

    // ---------- Envoi ----------

    public async Task<ReminderMessage?> BuildTodayMessageAsync(CancellationToken ct = default)
    {
        var today = await study.TodayAsync(ct);
        var plan = today.PlanDay;
        return DailyReminder.Build(plan?.PageFrom, plan?.PageTo, today.PagesDone, plan?.QuestionGoal ?? 0,
            today.QuestionsAnsweredToday, today.ErrorsDue, today.DaysToExam);
    }

    /// <summary>Envoie à tous les appareils ; supprime les abonnements expirés.</summary>
    public async Task<(int Sent, int Removed, int Failed)> SendToAllAsync(ReminderMessage message, CancellationToken ct = default)
    {
        var keys = await GetVapidKeysAsync(ct);
        var payload = JsonSerializer.Serialize(new { title = message.Title, body = message.Body, url = message.Url, tag = "pmp-daily" }, PayloadJson);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        int sent = 0, removed = 0, failed = 0;
        foreach (var device in await db.PushDevices.ToListAsync(ct))
        {
            switch (await sender.SendAsync(device, payload, keys, ct))
            {
                case PushResult.Sent: device.LastSuccessUtc = clock.UtcNow; sent++; break;
                case PushResult.Gone: db.PushDevices.Remove(device); removed++; break;
                default: failed++; break;
            }
        }
        await db.SaveChangesAsync(ct);
        return (sent, removed, failed);
    }

    /// <summary>Appelé chaque minute : envoie le rappel du jour à l'heure choisie (heure locale de l'utilisateur).</summary>
    public async Task<bool> RunDailyReminderAsync(CancellationToken ct = default)
    {
        var reminder = await GetReminderAsync(ct);
        var nowLocal = clock.ToLocal(clock.UtcNow);
        if (!DailyReminder.ShouldSend(reminder.Enabled, reminder.Time, reminder.LastSent, nowLocal)) return false;

        // Marqué avant l'envoi : pas de doublon si l'envoi échoue à moitié.
        await study.SetSettingAsync(SettingKeys.ReminderLastSent, clock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        var message = await BuildTodayMessageAsync(ct);
        if (message is null) return false; // tout est fait pour aujourd'hui
        await SendToAllAsync(message, ct);
        return true;
    }
}

public class DailyReminderService(IServiceScopeFactory scopes, ILogger<DailyReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<NotificationService>().RunDailyReminderAsync(stoppingToken))
                    logger.LogInformation("Rappel quotidien envoyé");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Rappel quotidien en échec");
            }
        }
    }
}
