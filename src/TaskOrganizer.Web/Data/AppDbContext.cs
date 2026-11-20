using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TaskOrganizer.Web.Domain;

namespace TaskOrganizer.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<TaskStatusChange> TaskStatusChanges => Set<TaskStatusChange>();
    public DbSet<TaskFieldChange> TaskFieldChanges => Set<TaskFieldChange>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(100).UseCollation("NOCASE");
            entity.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<WorkTask>(entity =>
        {
            entity.ToTable("Tasks");
            entity.Property(t => t.Title).HasMaxLength(1000);
            // Enums are stored as text so the table can be read and queried by hand.
            entity.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(t => t.Source).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(t => t.Project).WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(t => t.ImportKey).IsUnique();
            entity.HasIndex(t => t.Status);
        });

        modelBuilder.Entity<TaskStatusChange>(entity =>
        {
            entity.Property(c => c.FromStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.ToStatus).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(c => c.WorkTask).WithMany().HasForeignKey(c => c.WorkTaskId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskFieldChange>(entity =>
        {
            entity.Property(c => c.Field).HasConversion<string>().HasMaxLength(20);
            entity.Property(c => c.OldValue).HasMaxLength(50);
            entity.Property(c => c.NewValue).HasMaxLength(50);
            entity.HasOne(c => c.WorkTask).WithMany().HasForeignKey(c => c.WorkTaskId).OnDelete(DeleteBehavior.Restrict);
        });

        // The "only one open entry" rule is a unique index added by raw SQL in the
        // TaskFieldHistoryAndSingleOpenEntry migration; EF cannot describe an index on a constant expression.
        modelBuilder.Entity<TimeEntry>(entity =>
        {
            entity.HasIndex(e => e.Date);
        });

        modelBuilder.Entity<AppSettings>(entity =>
        {
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.HasData(new AppSettings());
        });

        MarkDateTimesAsUtc(modelBuilder);
    }

    /// <summary>SQLite returns DateTime values with an unspecified kind; every DateTime in this model is UTC.</summary>
    private static void MarkDateTimesAsUtc(ModelBuilder modelBuilder)
    {
        ValueConverter<DateTime, DateTime> utcConverter = new(
            value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        ValueConverter<DateTime?, DateTime?> nullableUtcConverter = new(
            value => value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(utcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableUtcConverter);
                }
            }
        }
    }
}
