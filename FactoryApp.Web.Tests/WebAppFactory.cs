using FactoryApp.Domain;
using FactoryApp.Domain.TestFixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FactoryApp.Web.Tests;

public class WebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        try
        {
            var options = new DbContextOptionsBuilder<FactoryDbContext>()
                .UseSqlServer(_container.GetConnectionString())
                .Options;
            await using var ctx = new FactoryDbContext(options);
            await ctx.Database.MigrateAsync();
            await FixtureSeeder.SeedAllAsync(ctx, isDevelopment: true);
        }
        catch
        {
            await _container.DisposeAsync();
            throw;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("FACTORY_CONNECTION", _container.GetConnectionString());
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }
}
