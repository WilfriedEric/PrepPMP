using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Core;
using PmpStudy.Infrastructure.Data;

namespace PmpStudy.Infrastructure.Services;

public class StudyClock(TimeProvider time, TimeZoneInfo zone) : IStudyClock
{
    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    public DateOnly Today => DateOnly.FromDateTime(ToLocal(UtcNow));
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    public (DateTime StartUtc, DateTime EndUtc) DayBoundsUtc(DateOnly day)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        return (start, end);
    }
}

/// <summary>PDF stocké sur disque, hors de wwwroot : il n'est servi que par l'endpoint authentifié.</summary>
public class LocalPdfStore(string path) : IPdfStore
{
    public Task<bool> ExistsAsync(CancellationToken ct = default) => Task.FromResult(File.Exists(path));

    public async Task SaveAsync(Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".upload";
        await using (var file = File.Create(temp))
            await content.CopyToAsync(file, ct);
        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(CancellationToken ct = default) =>
        Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true)
            : null);

    public Task<long?> GetLengthAsync(CancellationToken ct = default) =>
        Task.FromResult<long?>(File.Exists(path) ? new FileInfo(path).Length : null);
}

/// <summary>PDF dans un conteneur Azure Blob privé.</summary>
public class BlobPdfStore(BlobContainerClient container, string blobName) : IPdfStore
{
    private BlobClient Blob => container.GetBlobClient(blobName);

    public async Task<bool> ExistsAsync(CancellationToken ct = default) => (await Blob.ExistsAsync(ct)).Value;

    public async Task SaveAsync(Stream content, CancellationToken ct = default)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        await Blob.UploadAsync(content, overwrite: true, ct);
    }

    public async Task<Stream?> OpenReadAsync(CancellationToken ct = default) =>
        await ExistsAsync(ct) ? await Blob.OpenReadAsync(cancellationToken: ct) : null;

    public async Task<long?> GetLengthAsync(CancellationToken ct = default) =>
        await ExistsAsync(ct) ? (await Blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength : null;
}

/// <summary>Expose la fabrique du contexte concret (SQLite ou SQL Server) sous le type commun.</summary>
public class AppDbContextFactory<TContext>(IDbContextFactory<TContext> inner) : IDbContextFactory<AppDbContext>
    where TContext : AppDbContext
{
    public AppDbContext CreateDbContext() => inner.CreateDbContext();
}
