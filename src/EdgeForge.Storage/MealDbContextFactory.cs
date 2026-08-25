using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeForge.Storage;

/// <summary>
/// Design-time only. <c>dotnet ef</c> needs a way to construct the context without
/// booting the host - and booting this host would start the camera pipeline.
/// </summary>
/// <remarks>
/// The path below is a scratch file used while scaffolding a migration and is
/// never what the application opens; the real path comes from
/// <c>Storage:DatabasePath</c>.
/// </remarks>
internal sealed class MealDbContextFactory : IDesignTimeDbContextFactory<MealDbContext>
{
    public MealDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MealDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new MealDbContext(options);
    }
}
