using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace PmpStudy.Infrastructure.Data;

// Utilisées par « dotnet ef migrations add » (une série de migrations par fournisseur) et par les tests.

public static class IdentitySchema
{
    /// <summary>
    /// Identity lit la version du schéma (v3 : clés d'accès) dans les options de l'application.
    /// Hors de l'application (outils EF, tests), on fournit les mêmes options pour obtenir le même modèle.
    /// </summary>
    public static readonly IServiceProvider Services = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
        .BuildServiceProvider();
}

public class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteAppDbContext>()
            .UseSqlite("DataSource=design.db")
            .UseApplicationServiceProvider(IdentitySchema.Services)
            .Options);
}

public class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer("Server=(local);Database=PmpStudy;Trusted_Connection=True")
            .UseApplicationServiceProvider(IdentitySchema.Services)
            .Options);
}
