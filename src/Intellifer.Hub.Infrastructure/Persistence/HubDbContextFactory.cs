using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Intellifer.Hub.Infrastructure.Persistence;

public sealed class HubDbContextFactory : IDesignTimeDbContextFactory<HubDbContext>
{
    public HubDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Hub")
            ?? "Host=localhost;Port=5432;Database=intellifer_hub;Username=intellifer;Password=intellifer";
        var options = new DbContextOptionsBuilder<HubDbContext>();
        HubDbContext.Configure(options, connectionString);
        return new HubDbContext(options.Options);
    }
}
