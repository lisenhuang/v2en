using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace v2en.Data;

/// <summary>
/// Used by `dotnet ef` at design time so it can discover AppDbContext without
/// booting the full application (which requires an OpenRouter API key).
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=v2en-dev.db")
            .Options;
        return new AppDbContext(opts);
    }
}
