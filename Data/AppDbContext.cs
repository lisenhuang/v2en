using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace v2en.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Post> Posts => Set<Post>();
    public DbSet<FeedState> FeedStates => Set<FeedState>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY a DateTimeOffset stored in its default TEXT form — the trailing
        // offset breaks lexical ordering, so EF Core throws NotSupportedException on ORDER BY.
        // Every date in this app is already normalized to UTC at parse time (AtomParser uses
        // AdjustToUniversal), so we persist the UTC tick count: a plain INTEGER that sorts
        // correctly in SQL and round-trips the exact instant. Applies to both DateTimeOffset
        // and DateTimeOffset? properties across every entity.
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var post = modelBuilder.Entity<Post>();
        post.HasIndex(p => p.V2exId).IsUnique();
        post.HasIndex(p => p.Published);                       // homepage + feed reads
        post.HasIndex(p => new { p.Status, p.Published });     // worker "next pending, newest first"
        post.Property(p => p.SourceTagId).HasMaxLength(256);
        post.Property(p => p.SourceUrl).HasMaxLength(512);
        post.Property(p => p.AuthorName).HasMaxLength(256);
        post.Property(p => p.AuthorUri).HasMaxLength(512);
        post.Property(p => p.SourceContentHash).HasMaxLength(64);
        post.Property(p => p.Status).HasConversion<int>();

        // Singleton state row.
        modelBuilder.Entity<FeedState>().HasData(new FeedState { Id = 1 });
    }

    /// <summary>
    /// Stores a <see cref="DateTimeOffset"/> as its UTC tick count (INTEGER) so SQLite can
    /// sort/compare it. All app dates are UTC, so the offset carries no extra information.
    /// </summary>
    private sealed class DateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset, long>
    {
        public DateTimeOffsetToUtcTicksConverter()
            : base(d => d.UtcTicks, t => new DateTimeOffset(t, TimeSpan.Zero)) { }
    }
}
