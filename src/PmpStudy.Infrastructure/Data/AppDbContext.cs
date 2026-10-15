using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PmpStudy.Core.Entities;

namespace PmpStudy.Infrastructure.Data;

public class ApplicationUser : IdentityUser
{
}

/// <summary>
/// Contexte commun. Deux sous-classes portent chacune leurs migrations :
/// <see cref="SqliteAppDbContext"/> en local et <see cref="SqlServerAppDbContext"/> pour Azure SQL.
/// </summary>
public abstract class AppDbContext(DbContextOptions options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<EcoTask> EcoTasks => Set<EcoTask>();
    public DbSet<StudyPlanDay> PlanDays => Set<StudyPlanDay>();
    public DbSet<ReadingLog> ReadingLogs => Set<ReadingLog>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Scenario> Scenarios => Set<Scenario>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<StudySession> Sessions => Set<StudySession>();
    public DbSet<SessionSection> SessionSections => Set<SessionSection>();
    public DbSet<SessionItem> SessionItems => Set<SessionItem>();
    public DbSet<ErrorEntry> ErrorEntries => Set<ErrorEntry>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Chapter>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(400);
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<EcoTask>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
        });

        builder.Entity<StudyPlanDay>(e =>
        {
            e.HasIndex(x => x.Date).IsUnique();
            e.Property(x => x.Label).HasMaxLength(300);
            e.HasMany(x => x.Chapters).WithMany().UsingEntity("StudyPlanDayChapters");
            e.HasMany(x => x.EcoTasks).WithMany().UsingEntity("StudyPlanDayEcoTasks");
        });

        builder.Entity<ReadingLog>(e =>
        {
            e.HasIndex(x => x.Date);
            e.HasOne(x => x.StudyPlanDay).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Note>(e =>
        {
            e.HasIndex(x => x.Page);
            e.Property(x => x.Tags).HasMaxLength(400);
            e.HasOne(x => x.Chapter).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.EcoTask).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Question>(e =>
        {
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.Property(x => x.Source).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(8);
            e.HasOne(x => x.EcoTask).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Chapter).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Scenario).WithMany(x => x.Questions).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Options).WithOne(x => x.Question).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.CorrectCount);
        });

        builder.Entity<QuestionOption>(e => e.Property(x => x.MatchKey).HasMaxLength(100));

        builder.Entity<StudySession>(e =>
        {
            e.HasIndex(x => x.StartUtc);
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasMany(x => x.Sections).WithOne(x => x.StudySession).HasForeignKey(x => x.StudySessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Items).WithOne(x => x.StudySession).HasForeignKey(x => x.StudySessionId).OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.IsFinished);
        });

        builder.Entity<SessionSection>(e =>
        {
            e.HasIndex(x => new { x.StudySessionId, x.Index }).IsUnique();
            e.Ignore(x => x.IsLocked);
        });

        builder.Entity<SessionItem>(e =>
        {
            e.HasIndex(x => new { x.StudySessionId, x.Order }).IsUnique();
            e.HasIndex(x => x.AnsweredUtc);
            e.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ErrorEntry>(e =>
        {
            e.HasIndex(x => x.QuestionId).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextReview });
            e.Property(x => x.ExternalSource).HasMaxLength(200);
            e.HasOne(x => x.Question).WithMany().OnDelete(DeleteBehavior.SetNull);
            // Pas d'action en base : SQL Server refuse deux chemins de cascade EcoTask → ErrorEntry (direct et via Question).
            e.HasOne(x => x.EcoTask).WithMany().OnDelete(DeleteBehavior.ClientSetNull);
            e.Ignore(x => x.IsIncomplete);
        });

        builder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }
}

public class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options) : AppDbContext(options);

public class SqlServerAppDbContext(DbContextOptions<SqlServerAppDbContext> options) : AppDbContext(options);
