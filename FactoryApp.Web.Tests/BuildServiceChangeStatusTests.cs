using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BuildServiceChangeStatusTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string Station = "ASSEMBLY-1";
    private const string Operator = "op@example.com";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var ctx = CreateContext();
        await ctx.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public static IEnumerable<object[]> AllowedMoves() =>
    [
        [BuildStatus.Pending, BuildStatus.Running, "Started"],
        [BuildStatus.Running, BuildStatus.Complete, "Completed"],
        [BuildStatus.Running, BuildStatus.Failed, "Failed"],
        [BuildStatus.Failed, BuildStatus.Pending, "Retried"],
    ];

    public static IEnumerable<object[]> RejectedMoves() =>
    [
        [BuildStatus.Pending, BuildStatus.Complete],
        [BuildStatus.Complete, BuildStatus.Running],
        [BuildStatus.Pending, BuildStatus.Pending],
    ];

    public static IEnumerable<object[]> BadStationOrOperator() =>
    [
        ["", Operator],
        [" ", Operator],
        [new string('A', 51), Operator],
        [Station, ""],
        [Station, " "],
        [Station, new string('a', 257)],
    ];

    [Fact]
    public async Task Valid_move_writes_one_history_row()
    {
        var workflowInstanceId = Guid.NewGuid();
        var build = await SeedBuild(BuildStatus.Pending, workflowInstanceId);
        await using var ctx = CreateContext();
        var service = new BuildService(ctx);

        var result = await service.ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.NotNull(result.Event);
        Assert.Equal(build.Id, result.Event.BuildId);
        Assert.Equal(BuildStatus.Pending, result.Event.OldStatus);
        Assert.Equal(BuildStatus.Running, result.Event.NewStatus);

        Assert.Equal(BuildStatus.Running, (await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == build.Id)).Status);

        var history = await HistoryFor(build.Id);
        var row = Assert.Single(history);
        Assert.Equal("Started", row.EventType);
        Assert.Equal("Build", row.ActivityName);
        Assert.Equal("Pending", row.OldStatus);
        Assert.Equal("Running", row.NewStatus);
        Assert.Equal(build.Id, row.BuildId);
        Assert.Equal(workflowInstanceId, row.WorkflowInstanceId);
        Assert.Equal(Station, row.Station);
        Assert.Equal(Operator, row.Operator);
        Assert.Null(row.ElapsedMilliseconds);
        Assert.NotNull(row.ExecutionStarted);
        Assert.NotNull(row.ExecutionCompleted);
    }

    [Theory]
    [MemberData(nameof(AllowedMoves))]
    public async Task Each_allowed_move_sets_status_and_event_type(BuildStatus from, BuildStatus to, string eventType)
    {
        var build = await SeedBuild(from);
        var service = new BuildService(CreateContext());

        var result = await service.ChangeStatusAsync(build.Id, to, Station, Operator);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        await using (var ctx = CreateContext())
        {
            Assert.Equal(to, (await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == build.Id)).Status);
        }

        var row = Assert.Single(await HistoryFor(build.Id));
        Assert.Equal(eventType, row.EventType);
    }

    [Theory]
    [MemberData(nameof(RejectedMoves))]
    public async Task Invalid_move_writes_nothing(BuildStatus from, BuildStatus to)
    {
        var build = await SeedBuild(from);
        var service = new BuildService(CreateContext());

        var result = await service.ChangeStatusAsync(build.Id, to, Station, Operator);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal(Transitions.Validate(from, to), result.Error);
        Assert.Null(result.Event);

        await using (var ctx = CreateContext())
        {
            Assert.Equal(from, (await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == build.Id)).Status);
        }

        Assert.Empty(await HistoryFor(build.Id));
    }

    [Fact]
    public async Task Leaving_Running_records_elapsed()
    {
        var build = await SeedBuild(BuildStatus.Running, updatedAt: T0);
        var time = new FakeTime(T0.AddMilliseconds(1500));
        var service = new BuildService(CreateContext(), time);

        var result = await service.ChangeStatusAsync(build.Id, BuildStatus.Complete, Station, Operator);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var row = Assert.Single(await HistoryFor(build.Id));
        Assert.Equal("Completed", row.EventType);
        Assert.Equal(1500, row.ElapsedMilliseconds);
        Assert.Equal(T0, row.ExecutionStarted);
        Assert.Equal(T0.AddMilliseconds(1500), row.ExecutionCompleted);
    }

    [Fact]
    public async Task Null_workflow_instance_id_is_assigned_and_reused()
    {
        var build = await SeedBuild(BuildStatus.Pending, workflowInstanceId: null);
        var service = new BuildService(CreateContext());

        var first = await service.ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);
        Assert.Equal(TransitionOutcome.Applied, first.Outcome);

        Guid? assigned;
        await using (var ctx = CreateContext())
        {
            assigned = (await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == build.Id)).WorkflowInstanceId;
        }
        Assert.NotNull(assigned);
        Assert.NotEqual(Guid.Empty, assigned.Value);

        var second = await service.ChangeStatusAsync(build.Id, BuildStatus.Complete, Station, Operator);
        Assert.Equal(TransitionOutcome.Applied, second.Outcome);

        await using (var ctx = CreateContext())
        {
            Assert.Equal(assigned, (await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == build.Id)).WorkflowInstanceId);
        }

        var history = await HistoryFor(build.Id);
        Assert.Equal(2, history.Count);
        Assert.All(history, row => Assert.Equal(assigned, row.WorkflowInstanceId));
    }

    [Fact]
    public async Task Unknown_build_returns_NotFound_and_writes_nothing()
    {
        var service = new BuildService(CreateContext());
        var missingId = Guid.NewGuid();

        var result = await service.ChangeStatusAsync(missingId, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.NotFound, result.Outcome);
        Assert.Equal("Build not found.", result.Error);
        Assert.Null(result.Event);
        Assert.Empty(await HistoryFor(missingId));
    }

    [Theory]
    [MemberData(nameof(BadStationOrOperator))]
    public async Task Blank_or_oversized_station_or_operator_is_rejected_and_writes_nothing(string station, string operatorName)
    {
        var build = await SeedBuild(BuildStatus.Pending);
        var service = new BuildService(CreateContext());

        var result = await service.ChangeStatusAsync(build.Id, BuildStatus.Running, station, operatorName);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Null(result.Event);

        await using (var ctx = CreateContext())
        {
            Assert.Equal(BuildStatus.Pending, (await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == build.Id)).Status);
        }

        Assert.Empty(await HistoryFor(build.Id));
    }

    [Fact]
    public async Task Stale_status_is_rejected()
    {
        var build = await SeedBuild(BuildStatus.Pending);

        var first = await new BuildService(CreateContext())
            .ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);
        var second = await new BuildService(CreateContext())
            .ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.Applied, first.Outcome);
        Assert.Equal(TransitionOutcome.Invalid, second.Outcome);
        Assert.Single(await HistoryFor(build.Id));
    }

    private FactoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlServer(_container.GetConnectionString())
            .Options;
        return new FactoryDbContext(options);
    }

    private async Task<Build> SeedBuild(BuildStatus status, Guid? workflowInstanceId = null, DateTime? updatedAt = null)
    {
        var timestamp = updatedAt ?? T0;
        var build = new Build
        {
            Id = Guid.NewGuid(),
            Name = "Test build",
            Status = status,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            WorkflowInstanceId = workflowInstanceId,
        };

        await using var ctx = CreateContext();
        ctx.Builds.Add(build);
        await ctx.SaveChangesAsync();
        return build;
    }

    private async Task<List<WorkflowHistoryRecord>> HistoryFor(Guid buildId)
    {
        await using var ctx = CreateContext();
        return await ctx.WorkflowHistory.AsNoTracking().Where(h => h.BuildId == buildId).ToListAsync();
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
