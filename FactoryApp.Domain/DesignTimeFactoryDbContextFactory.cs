using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FactoryApp.Domain;

/// <summary>Lets `dotnet ef` build the context before a web host exists. Reads FACTORY_CONNECTION.</summary>
public class DesignTimeFactoryDbContextFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FACTORY_CONNECTION")
            ?? throw new InvalidOperationException("Set FACTORY_CONNECTION (see .env.example).");
        var options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connection).Options;
        return new FactoryDbContext(options);
    }
}
