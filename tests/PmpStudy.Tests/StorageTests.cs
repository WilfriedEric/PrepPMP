using Microsoft.Extensions.Caching.Memory;
using PmpStudy.Web.Services;

namespace PmpStudy.Tests;

public class DatabaseStorageTests
{
    [Fact]
    public async Task Course_pdf_is_stored_in_the_database_and_served_seekable()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new DbPdfStore(db, cache, db.Clock);
        Assert.False(await store.ExistsAsync());

        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        await store.SaveAsync(new MemoryStream(bytes));
        Assert.True(await store.ExistsAsync());
        Assert.Equal(5000, await store.GetLengthAsync());

        await using var stream = (await store.OpenReadAsync())!;
        Assert.True(stream.CanSeek); // nécessaire pour les requêtes partielles de PDF.js
        stream.Seek(4000, SeekOrigin.Begin);
        Assert.Equal(bytes[4000], (byte)stream.ReadByte());

        // Remplacement : le cache est vidé, la nouvelle version est servie.
        await store.SaveAsync(new MemoryStream([1, 2, 3]));
        await using var replaced = (await store.OpenReadAsync())!;
        Assert.Equal(3, replaced.Length);
    }

    [Fact]
    public async Task Time_zone_comes_from_the_database_and_is_reloaded_after_change()
    {
        await using var db = await TestDb.CreateAsync(seedQuestions: false);
        var zones = new TimeZoneSetting(db);
        var study = new StudyService(db, db.Clock, zones);

        Assert.Equal(TimeZoneSetting.Resolve(TimeZoneSetting.DefaultId)!.BaseUtcOffset, zones.Current.BaseUtcOffset);
        await study.SetSettingAsync(SettingKeys.TimeZone, "Africa/Lagos");
        Assert.Equal(TimeSpan.FromHours(1), zones.Current.BaseUtcOffset);

        var clock = new StudyClock(TimeProvider.System, () => zones.Current);
        var nowUtc = DateTime.UtcNow;
        Assert.Equal(nowUtc.AddHours(1).Hour, clock.ToLocal(nowUtc).Hour);
    }

    [Theory]
    [InlineData("Africa/Abidjan")]
    [InlineData("Europe/Paris")]
    [InlineData("UTC")]
    public void Time_zone_ids_resolve(string id) => Assert.NotNull(TimeZoneSetting.Resolve(id));
}

public class DatabaseProviderTests
{
    [Theory]
    [InlineData("Sqlite", "Server=mssql.example.com;Database=pmp;User Id=u;Password=p;TrustServerCertificate=True", "SqlServer")]
    [InlineData(null, "Data Source=sql5.example.net,1433;Initial Catalog=pmp;User ID=u;Password=p", "SqlServer")]
    [InlineData("SqlServer", "DataSource=App_Data/pmpstudy.db;Cache=Shared", "Sqlite")]
    [InlineData("Sqlite", "", "Sqlite")]
    [InlineData(null, null, "SqlServer")]
    public void Provider_follows_the_connection_string(string? configured, string? connection, string expected) =>
        Assert.Equal(expected, PmpStudy.Web.Services.DatabaseProvider.Resolve(configured, connection));
}
