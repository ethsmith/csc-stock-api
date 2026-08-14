using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Csx.Infrastructure.Data;

public sealed class CsxDbContextFactory : IDesignTimeDbContextFactory<CsxDbContext>
{
    public CsxDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("CSX_CONNECTION")
                 ?? "Host=localhost;Port=5433;Database=csx;Username=csx;Password=csx";
        var options = new DbContextOptionsBuilder<CsxDbContext>()
            .UseNpgsql(cs)
            .Options;
        return new CsxDbContext(options);
    }
}
