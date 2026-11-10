using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Testcontainers.MsSql;

namespace FactoryApp.Web.Tests;

public sealed class SqlFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var ctx = new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);
        await ctx.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public class BuildServiceChangeStatusTests(SqlFixture sql) : IClassFixture<SqlFixture>
{
    private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string Station = "ASSEMBLY-1";
    private const string Operator = "op@example.com";

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

        Assert.Equal(BuildStatus.Running, (await StoredBuild(build.Id)).Status);

        var row = Assert.Single(await HistoryFor(build.Id));
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
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx).ChangeStatusAsync(build.Id, to, Station, Operator);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Equal(to, (await StoredBuild(build.Id)).Status);

        var row = Assert.Single(await HistoryFor(build.Id));
        Assert.Equal(eventType, row.EventType);
    }

    [Theory]
    [MemberData(nameof(RejectedMoves))]
    public async Task Invalid_move_writes_nothing(BuildStatus from, BuildStatus to)
    {
        var build = await SeedBuild(from);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx).ChangeStatusAsync(build.Id, to, Station, Operator);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal(Transitions.Validate(from, to), result.Error);
        Assert.Null(result.Event);
        Assert.Equal(from, (await StoredBuild(build.Id)).Status);
        Assert.Empty(await HistoryFor(build.Id));
    }

    [Fact]
    public async Task Leaving_Running_records_elapsed()
    {
        var build = await SeedBuild(BuildStatus.Running, updatedAt: T0);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0.AddMilliseconds(1500)))
            .ChangeStatusAsync(build.Id, BuildStatus.Complete, Station, Operator);

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
        await using var ctx = CreateContext();
        var service = new BuildService(ctx);

        var first = await service.ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);
        Assert.Equal(TransitionOutcome.Applied, first.Outcome);

        var assigned = (await StoredBuild(build.Id)).WorkflowInstanceId;
        Assert.NotNull(assigned);
        Assert.NotEqual(Guid.Empty, assigned.Value);

        var second = await service.ChangeStatusAsync(build.Id, BuildStatus.Complete, Station, Operator);
        Assert.Equal(TransitionOutcome.Applied, second.Outcome);
        Assert.Equal(assigned, (await StoredBuild(build.Id)).WorkflowInstanceId);

        var history = await HistoryFor(build.Id);
        Assert.Equal(2, history.Count);
        Assert.All(history, row => Assert.Equal(assigned, row.WorkflowInstanceId));
    }

    [Fact]
    public async Task Empty_workflow_instance_id_is_replaced_and_reused()
    {
        var build = await SeedBuild(BuildStatus.Pending, workflowInstanceId: Guid.Empty);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx).ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var assigned = (await StoredBuild(build.Id)).WorkflowInstanceId;
        Assert.NotNull(assigned);
        Assert.NotEqual(Guid.Empty, assigned.Value);

        var row = Assert.Single(await HistoryFor(build.Id));
        Assert.Equal(assigned, row.WorkflowInstanceId);
    }

    [Fact]
    public async Task Unknown_build_returns_NotFound_and_writes_nothing()
    {
        await using var ctx = CreateContext();
        var missingId = Guid.NewGuid();

        var result = await new BuildService(ctx).ChangeStatusAsync(missingId, BuildStatus.Running, Station, Operator);

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
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx).ChangeStatusAsync(build.Id, BuildStatus.Running, station, operatorName);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Null(result.Event);
        Assert.Equal(BuildStatus.Pending, (await StoredBuild(build.Id)).Status);
        Assert.Empty(await HistoryFor(build.Id));
    }

    [Fact]
    public async Task Repeated_move_is_rejected_by_transitions()
    {
        var build = await SeedBuild(BuildStatus.Pending);

        await using (var first = CreateContext())
        {
            var applied = await new BuildService(first).ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);
            Assert.Equal(TransitionOutcome.Applied, applied.Outcome);
        }

        await using var second = CreateContext();
        var repeated = await new BuildService(second).ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.Invalid, repeated.Outcome);
        Assert.Equal(Transitions.Validate(BuildStatus.Running, BuildStatus.Running), repeated.Error);
        Assert.Single(await HistoryFor(build.Id));
    }

    [Fact]
    public async Task Status_changed_before_guarded_update_is_rejected()
    {
        var build = await SeedBuild(BuildStatus.Pending);
        await using var ctx = CreateContext(new ExternalChangeBeforeUpdate(sql.ConnectionString, conn =>
            ExecuteAsync(conn, "UPDATE dbo.Builds SET Status = @status WHERE Id = @id",
                ("@status", (int)BuildStatus.Running), ("@id", build.Id))));

        var result = await new BuildService(ctx).ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Build status changed; reload and retry.", result.Error);
        Assert.Null(result.Event);
        Assert.Equal(BuildStatus.Running, (await StoredBuild(build.Id)).Status);
        Assert.Empty(await HistoryFor(build.Id));
    }

    [Fact]
    public async Task Stale_snapshot_with_same_status_is_rejected_by_guarded_update()
    {
        var build = await SeedBuild(BuildStatus.Pending);
        var later = T0.AddSeconds(5);
        await using var ctx = CreateContext(new ExternalChangeBeforeUpdate(sql.ConnectionString, conn =>
            ExecuteAsync(conn, "UPDATE dbo.Builds SET UpdatedAt = @at WHERE Id = @id",
                ("@at", later), ("@id", build.Id))));

        var result = await new BuildService(ctx).ChangeStatusAsync(build.Id, BuildStatus.Running, Station, Operator);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Build status changed; reload and retry.", result.Error);
        var stored = await StoredBuild(build.Id);
        Assert.Equal(BuildStatus.Pending, stored.Status);
        Assert.Equal(later, stored.UpdatedAt);
        Assert.Empty(await HistoryFor(build.Id));
    }

    private static FactoryDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }
        return new FactoryDbContext(builder.Options);
    }

    private FactoryDbContext CreateContext(IInterceptor? interceptor = null) =>
        CreateContext(sql.ConnectionString, interceptor is null ? [] : [interceptor]);

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

    private static async Task ExecuteAsync(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // Runs once, on a separate connection, just before the service's first UPDATE on Builds.
    private sealed class ExternalChangeBeforeUpdate(string connectionString, Func<SqlConnection, Task> change)
        : DbCommandInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired
                && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("[Builds]", StringComparison.Ordinal))
            {
                _fired = true;
                await using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(cancellationToken);
                await change(conn);
            }
            return result;
        }
    }
}
