using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OneArr.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to create migrations. The runtime database path comes from ONEARR_CONFIG_DIR.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OneArrDbContext>
{
    public OneArrDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OneArrDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
