using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BuildServiceCreateBuildTests(SqlFixture sql) : IClassFixture<SqlFixture>
{
    private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

    // Rejected creates use their own timestamp so the count below cannot see rows from other tests.
    private static readonly DateTime RejectedAt = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> InvalidNames() =>
    [
        [""],
        [" "],
        [new string('A', 257)],
    ];

    [Fact]
    public async Task Valid_name_creates_pending_build_with_workflow_id_and_no_history()
    {
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0)).CreateBuildAsync("  Press Line 4  ", "Night shift");

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Null(result.Error);
        Assert.NotNull(result.BuildId);

        var stored = await StoredBuild(result.BuildId.Value);
        Assert.Equal(BuildStatus.Pending, stored.Status);
        Assert.Equal("Press Line 4", stored.Name);
        Assert.Equal("Night shift", stored.Description);
        Assert.Equal(T0, stored.CreatedAt);
        Assert.Equal(T0, stored.UpdatedAt);
        Assert.NotNull(stored.WorkflowInstanceId);
        Assert.NotEqual(Guid.Empty, stored.WorkflowInstanceId.Value);
        Assert.Empty(await HistoryFor(result.BuildId.Value));
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task Blank_or_oversized_name_is_rejected_and_nothing_is_written(string name)
    {
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(RejectedAt)).CreateBuildAsync(name);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Null(result.BuildId);
        Assert.Equal(0, await CountBuildsAt(RejectedAt));
    }

    [Fact]
    public async Task Oversized_description_is_rejected_and_nothing_is_written()
    {
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(RejectedAt))
            .CreateBuildAsync("Press Line 4", new string('d', 1001));

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Null(result.BuildId);
        Assert.Equal(0, await CountBuildsAt(RejectedAt));
    }

    private FactoryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<Build> StoredBuild(Guid buildId)
    {
        await using var ctx = CreateContext();
        return await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == buildId);
    }

    private async Task<List<WorkflowHistoryRecord>> HistoryFor(Guid buildId)
    {
        await using var ctx = CreateContext();
        return await ctx.WorkflowHistory.AsNoTracking().Where(h => h.BuildId == buildId).ToListAsync();
    }

    private async Task<int> CountBuildsAt(DateTime createdAt)
    {
        await using var ctx = CreateContext();
        return await ctx.Builds.CountAsync(b => b.CreatedAt == createdAt);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
