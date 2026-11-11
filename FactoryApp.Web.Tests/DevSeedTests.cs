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
        var factory = new DevSeedFactory();
        await factory.InitializeAsync();
        try
        {
            using var client = factory.CreateClient();

            var options = new DbContextOptionsBuilder<FactoryDbContext>()
                .UseSqlServer(factory.ConnectionString)
                .Options;
            await using var ctx = new FactoryDbContext(options);

            Assert.True(await ctx.AuthUsers.CountAsync() > 0);
            Assert.Equal(18, await ctx.Builds.CountAsync());
            Assert.Equal(16, await ctx.TestRuns.CountAsync());
            Assert.Equal(51, await ctx.WorkflowHistory.CountAsync());
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    private sealed class DevSeedFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        private readonly MsSqlContainer _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        public string ConnectionString => _container.GetConnectionString();

        public Task InitializeAsync() => _container.StartAsync();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("FACTORY_CONNECTION", _container.GetConnectionString());
            builder.UseSetting("FACTORY_DEV_SEED", "true");
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
