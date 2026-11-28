using System.Text.Json;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using FactoryApp.Domain.TestFixtures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class FixtureSeederHistoryTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder(SqlServerImage.Reference).Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Seed_twice_does_not_duplicate()
    {
        var connectionString = await CreateMigratedDatabaseAsync();

        await using (var ctx = CreateContext(connectionString))
        {
            await FixtureSeeder.SeedAllAsync(ctx, isDevelopment: true);
        }

        var first = await CountsAsync(connectionString);

        await using (var ctx = CreateContext(connectionString))
        {
            await FixtureSeeder.SeedAllAsync(ctx, isDevelopment: true);
        }

        var second = await CountsAsync(connectionString);

        Assert.Equal(first.Users, second.Users);
        Assert.Equal(first.Builds, second.Builds);
        Assert.Equal(first.Parts, second.Parts);
        Assert.Equal(first.TestRuns, second.TestRuns);
        Assert.Equal(first.History, second.History);
        Assert.Equal(first.HistoryIds, second.HistoryIds);
        Assert.True(first.History > 0);
        Assert.Equal(51, first.History);
        Assert.Equal(16, first.TestRuns);
        Assert.Equal(18, first.Builds);
    }

    [Fact]
    public async Task History_matches_seeded_statuses()
    {
        var (builds, runs, history) = await SeedAndLoadAsync();

        foreach (var build in builds)
        {
            var rows = history.Where(h => h.ActivityName == "Build" && h.BuildId == build.Id).ToList();
            if (rows.Count == 0)
            {
                Assert.Equal(BuildStatus.Pending, build.Status);
                continue;
            }

            AssertWalk(build.Status, rows, (BuildStatus from, BuildStatus to) => Transitions.IsAllowed(from, to));
            AssertTiming(rows);
        }

        var rowsByRun = history
            .Where(h => h.ActivityName == "TestRun")
            .GroupBy(h => JsonDocument.Parse(h.StateSnapshot!).RootElement.GetProperty("testRunId").GetGuid())
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var run in runs)
        {
            var rows = rowsByRun.GetValueOrDefault(run.Id) ?? [];
            Assert.NotEmpty(rows);

            AssertWalk(run.Status, rows, (TestStatus from, TestStatus to) => Transitions.IsAllowed(from, to));
            AssertTiming(rows);
            Assert.Equal(run.CompletedAt, rows.SingleOrDefault(r => r.OldStatus == "Running")?.RecordedAt);
        }

        var buildsById = builds.ToDictionary(b => b.Id);
        foreach (var row in history.Where(h => h.BuildId.HasValue))
        {
            var build = buildsById[row.BuildId!.Value];
            Assert.NotNull(build.WorkflowInstanceId);
            Assert.Equal<Guid?>(build.WorkflowInstanceId, row.WorkflowInstanceId);
        }
    }

    [Fact]
    public async Task History_has_spread_and_valid_dimensions()
    {
        var (_, runs, history) = await SeedAndLoadAsync();

        var stations = new HashSet<string> { "ASSEMBLY-1", "TEST-BAY-1", "TEST-BAY-2" };
        var operators = new HashSet<string> { "test@example.com", "admin@example.com", "user@example.com" };
        var failureCodes = new HashSet<string> { "LEAK", "OVERPRESSURE", "SENSOR_FAULT", "TIMEOUT", "OTHER" };

        foreach (var row in history)
        {
            Assert.Contains(row.Station!, stations);
            Assert.Contains(row.Operator!, operators);
            if (row.ActivityName == "Build")
            {
                Assert.Equal("ASSEMBLY-1", row.Station);
            }
            else
            {
                Assert.StartsWith("TEST-BAY", row.Station);
            }
        }

        foreach (var run in runs)
        {
            if (run.Status == TestStatus.Failed)
            {
                Assert.Contains(run.FailureCode!, failureCodes);
            }
            else
            {
                Assert.Null(run.FailureCode);
            }
        }

        var seenCodes = runs.Select(r => r.FailureCode).Where(c => c != null).Select(c => c!).ToHashSet();
        Assert.True(failureCodes.SetEquals(seenCodes));

        var buildElapsed = history
            .Where(h => h.ActivityName == "Build" && h.ElapsedMilliseconds != null)
            .Select(h => h.ElapsedMilliseconds)
            .Distinct()
            .Count();
        var testRunElapsed = history
            .Where(h => h.ActivityName == "TestRun" && h.ElapsedMilliseconds != null)
            .Select(h => h.ElapsedMilliseconds)
            .Distinct()
            .Count();
        Assert.True(buildElapsed >= 5, $"distinct build elapsed values: {buildElapsed}");
        Assert.True(testRunElapsed >= 5, $"distinct test run elapsed values: {testRunElapsed}");

        var span = history.Max(h => h.RecordedAt) - history.Min(h => h.RecordedAt);
        Assert.True(span >= TimeSpan.FromDays(14), $"history span: {span}");
    }

    [Fact]
    public async Task SeedAll_refuses_non_development()
    {
        var connectionString = await CreateMigratedDatabaseAsync();

        await using var ctx = CreateContext(connectionString);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureSeeder.SeedAllAsync(ctx, isDevelopment: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FixtureSeeder.CleanupAllAsync(ctx, isDevelopment: false));

        Assert.Empty(await ctx.AuthUsers.ToListAsync());
    }

    [Fact]
    public async Task Cleanup_removes_history()
    {
        var connectionString = await CreateMigratedDatabaseAsync();

        await using (var ctx = CreateContext(connectionString))
        {
            await FixtureSeeder.SeedAllAsync(ctx, isDevelopment: true);
        }

        await using (var ctx = CreateContext(connectionString))
        {
            await FixtureSeeder.CleanupAllAsync(ctx, isDevelopment: true);
        }

        await using var verify = CreateContext(connectionString);
        Assert.Equal(0, await verify.WorkflowHistory.CountAsync());
        Assert.Equal(0, await verify.TestRuns.CountAsync());
        Assert.Equal(0, await verify.Builds.CountAsync());
    }

    private static void AssertWalk<TStatus>(
        TStatus final,
        IEnumerable<WorkflowHistoryRecord> rows,
        Func<TStatus, TStatus, bool> isAllowed) where TStatus : struct, Enum
    {
        var current = Enum.Parse<TStatus>("Pending");
        foreach (var row in rows.OrderBy(r => r.RecordedAt))
        {
            Assert.Equal(current.ToString(), row.OldStatus);
            var next = Enum.Parse<TStatus>(row.NewStatus);
            Assert.True(isAllowed(current, next), $"{current} -> {next} is not an allowed transition");
            current = next;
        }

        Assert.Equal(final.ToString(), current.ToString());
    }

    private static void AssertTiming(IReadOnlyCollection<WorkflowHistoryRecord> rows)
    {
        var started = rows.Single(r => r.OldStatus == "Pending");
        Assert.Null(started.ElapsedMilliseconds);
        Assert.Equal(started.RecordedAt, started.ExecutionStarted);
        Assert.Equal(started.RecordedAt, started.ExecutionCompleted);

        foreach (var terminal in rows.Where(r => r.OldStatus == "Running"))
        {
            var expectedMs = (long)(terminal.RecordedAt - started.RecordedAt).TotalMilliseconds;
            Assert.Equal<long?>(expectedMs, terminal.ElapsedMilliseconds);
            Assert.Equal(started.RecordedAt, terminal.ExecutionStarted);
            Assert.Equal(terminal.RecordedAt, terminal.ExecutionCompleted);
            Assert.Equal(expectedMs, (long)(terminal.ExecutionCompleted!.Value - terminal.ExecutionStarted!.Value).TotalMilliseconds);
        }
    }

    private async Task<(List<Build> Builds, List<TestRun> Runs, List<WorkflowHistoryRecord> History)> SeedAndLoadAsync()
    {
        var connectionString = await CreateMigratedDatabaseAsync();

        await using (var seedCtx = CreateContext(connectionString))
        {
            await FixtureSeeder.SeedAllAsync(seedCtx, isDevelopment: true);
        }

        await using var ctx = CreateContext(connectionString);
        return (
            await ctx.Builds.AsNoTracking().ToListAsync(),
            await ctx.TestRuns.AsNoTracking().ToListAsync(),
            await ctx.WorkflowHistory.AsNoTracking().ToListAsync());
    }

    private static async Task<(int Users, int Builds, int Parts, int TestRuns, int History, string[] HistoryIds)> CountsAsync(string connectionString)
    {
        await using var ctx = CreateContext(connectionString);
        var historyIds = await ctx.WorkflowHistory.Select(h => h.Id.ToString()).OrderBy(id => id).ToArrayAsync();
        return (
            await ctx.AuthUsers.CountAsync(),
            await ctx.Builds.CountAsync(),
            await ctx.Parts.CountAsync(),
            await ctx.TestRuns.CountAsync(),
            historyIds.Length,
            historyIds);
    }

    private async Task<string> CreateMigratedDatabaseAsync()
    {
        var databaseName = $"seed_{Guid.NewGuid():N}";
        var serverConnectionString = _container.GetConnectionString();

        await using (var connection = new SqlConnection(serverConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        await using var ctx = CreateContext(connectionString);
        await ctx.Database.MigrateAsync();
        return connectionString;
    }

    private static FactoryDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new FactoryDbContext(options);
    }
}
