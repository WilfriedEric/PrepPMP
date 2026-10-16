using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PmpStudy.Web.Data;
using PmpStudy.Web.Model;
using PmpStudy.Web.Model.Entities;

namespace PmpStudy.Web.Services;

/// <summary>Heure du serveur, convertie dans le fuseau choisi en base (réglage « TimeZone »).</summary>
public class StudyClock(TimeProvider time, Func<TimeZoneInfo> zone) : IStudyClock
{
    public StudyClock(TimeProvider time, TimeZoneInfo fixedZone) : this(time, () => fixedZone) { }

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    public DateOnly Today => DateOnly.FromDateTime(ToLocal(UtcNow));
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone());

    public (DateTime StartUtc, DateTime EndUtc) DayBoundsUtc(DateOnly day)
    {
        var z = zone();
        var start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), z);
        var end = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), z);
        return (start, end);
    }
}

/// <summary>Fuseau horaire lu en base et gardé en mémoire ; relu après chaque modification du réglage.</summary>
public class TimeZoneSetting(IDbContextFactory<AppDbContext> dbFactory)
{
    public const string DefaultId = "Africa/Abidjan";
    private TimeZoneInfo? cached;

    public TimeZoneInfo Current
    {
        get
        {
            if (cached is not null) return cached;
            string? id = null;
            try
            {
                using var db = dbFactory.CreateDbContext();
                id = db.Settings.AsNoTracking().Where(s => s.Key == SettingKeys.TimeZone).Select(s => s.Value).FirstOrDefault();
            }
            catch (Exception)
            {
                // Base pas encore créée (premier démarrage) : fuseau par défaut, sans le mémoriser.
                return Resolve(DefaultId) ?? TimeZoneInfo.Utc;
            }
            return cached = Resolve(id ?? DefaultId) ?? TimeZoneInfo.Utc;
        }
    }

    public void Invalidate() => cached = null;

    /// <summary>Accepte un identifiant IANA (« Africa/Abidjan ») ou Windows (« Greenwich Standard Time »).</summary>
    public static TimeZoneInfo? Resolve(string id)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone)) return zone;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out zone)) return zone;
        return null;
    }
}

/// <summary>
/// PDF du cours stocké dans la base (table CourseFiles) : rien sur le disque du serveur,
/// adapté à un hébergement mutualisé. Gardé en mémoire pour servir vite les requêtes partielles de PDF.js.
/// </summary>
public class DbPdfStore(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache, IStudyClock clock) : IPdfStore
{
    public const string Kind = "course-pdf";
    private const string CacheKey = "course-pdf-bytes";

    public async Task<bool> ExistsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CourseFiles.AnyAsync(f => f.Kind == Kind, ct);
    }

    public async Task SaveAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.CourseFiles.FirstOrDefaultAsync(f => f.Kind == Kind, ct);
        if (file is null)
        {
            file = new CourseFile { Kind = Kind };
            db.CourseFiles.Add(file);
        }
        file.Content = buffer.ToArray();
        file.Length = file.Content.LongLength;
        file.ContentType = "application/pdf";
        file.UploadedUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }

    public async Task<Stream?> OpenReadAsync(CancellationToken ct = default)
    {
        var bytes = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.CourseFiles.AsNoTracking().Where(f => f.Kind == Kind).Select(f => f.Content).FirstOrDefaultAsync(ct);
        });
        if (bytes is null) cache.Remove(CacheKey);
        return bytes is null ? null : new MemoryStream(bytes, writable: false);
    }

    public async Task<long?> GetLengthAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CourseFiles.Where(f => f.Kind == Kind).Select(f => (long?)f.Length).FirstOrDefaultAsync(ct);
    }
}

/// <summary>Expose la fabrique du contexte concret (SQLite ou SQL Server) sous le type commun.</summary>
public class AppDbContextFactory<TContext>(IDbContextFactory<TContext> inner) : IDbContextFactory<AppDbContext>
    where TContext : AppDbContext
{
    public AppDbContext CreateDbContext() => inner.CreateDbContext();
}
