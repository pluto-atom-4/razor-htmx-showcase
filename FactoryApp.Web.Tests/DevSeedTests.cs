using FactoryApp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class DevSeedTests
{
    [Fact]
    public async Task Dev_seed_flag_migrates_and_seeds_an_empty_database()
    {
        await using var run = await StartedHostAsync("Development", "true");

        await using var ctx = run.Context();

        Assert.Equal(3, await ctx.AuthUsers.CountAsync());
        Assert.Equal(18, await ctx.Builds.CountAsync());
        Assert.Equal(16, await ctx.TestRuns.CountAsync());
        Assert.Equal(51, await ctx.WorkflowHistory.CountAsync());
    }

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Development", "false")]
    [InlineData("Development", null)]
    public async Task Host_does_not_migrate_or_seed_unless_development_and_flag_true(string environment, string? flag)
    {
        await using var run = await StartedHostAsync(environment, flag);

        await using var ctx = run.Context();

        Assert.Empty(await ctx.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<StartedHost> StartedHostAsync(string environment, string? flag)
    {
        var factory = new DevSeedFactory(environment, flag);
        try
        {
            await factory.InitializeAsync();
            // Building the host runs the startup seed block in Program.cs.
            _ = factory.CreateClient();
            return new StartedHost(factory);
        }
        catch
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
            throw;
        }
    }

    private sealed class StartedHost(DevSeedFactory factory) : IAsyncDisposable
    {
        public FactoryDbContext Context() => new(
            new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(factory.ConnectionString).Options);

        public async ValueTask DisposeAsync() => await ((IAsyncLifetime)factory).DisposeAsync();
    }

    private sealed class DevSeedFactory(string environment, string? flag) : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly MsSqlContainer _container = new MsSqlBuilder(SqlServerImage.Reference)
            .Build();

        public string ConnectionString => _container.GetConnectionString();

        public Task InitializeAsync() => _container.StartAsync();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("FACTORY_CONNECTION", _container.GetConnectionString());
            if (flag is not null)
            {
                builder.UseSetting("FACTORY_DEV_SEED", flag);
            }
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
}
