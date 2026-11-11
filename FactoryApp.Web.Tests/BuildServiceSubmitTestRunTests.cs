using System.Data.Common;
using System.Text.Json;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BuildServiceSubmitTestRunTests(SqlFixture sql) : IClassFixture<SqlFixture>
{
    private static readonly DateTime T0 = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string Station = "TEST-BAY-1";
    private const string Operator = "op@example.com";

    private const string StationError = "Station is required and must be at most 50 characters.";
    private const string OperatorError = "Operator is required and must be at most 256 characters.";

    public static IEnumerable<object[]> FailureCodeValues() =>
        FailureCodes.All.Select(code => new object[] { code });

    public static IEnumerable<object?[]> MissingFailureCodes() =>
    [
        [null],
        [""],
        [" "],
        ["   "],
    ];

    public static IEnumerable<object[]> BlankPassedCodes() =>
    [
        [""],
        ["   "],
    ];

    public static IEnumerable<object[]> UnknownFailureCodes() =>
    [
        ["BOGUS"],
        ["leak"],
    ];

    public static IEnumerable<object[]> RejectedResults() =>
    [
        [TestStatus.Pending],
        [TestStatus.Running],
        [(TestStatus)99],
    ];

    public static IEnumerable<object[]> NotRunningStatuses() =>
    [
        [BuildStatus.Pending],
        [BuildStatus.Complete],
        [BuildStatus.Failed],
    ];

    public static IEnumerable<object[]> BadStations() =>
    [
        ["", StationError],
        [" ", StationError],
        [new string('A', 51), StationError],
    ];

    public static IEnumerable<object[]> BadOperators() =>
    [
        ["", OperatorError],
        [" ", OperatorError],
        [new string('a', 257), OperatorError],
    ];

    [Theory]
    [MemberData(nameof(FailureCodeValues))]
    public async Task Failed_run_records_code_and_two_history_rows(string code)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Failed, Station, Operator, code);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        Assert.Null(result.Event);

        var run = Assert.Single(await TestRunsFor(build.Id));
        Assert.Equal(TestStatus.Failed, run.Status);
        Assert.Equal(code, run.FailureCode);
        Assert.Equal($"Failed: {code}", run.Result);
        Assert.Equal(T0, run.CompletedAt);

        var history = await HistoryFor(build.Id);
        Assert.Equal(2, history.Count);

        var started = Assert.Single(history, h => h.EventType == "Started");
        Assert.Equal("Pending", started.OldStatus);
        Assert.Equal("Running", started.NewStatus);
        Assert.Null(started.ElapsedMilliseconds);
        AssertTestRunRow(started, build, run.Id);

        var failed = Assert.Single(history, h => h.EventType == "Failed");
        Assert.Equal("Running", failed.OldStatus);
        Assert.Equal("Failed", failed.NewStatus);
        Assert.Equal(0, failed.ElapsedMilliseconds);
        Assert.Null(failed.ErrorMessage);
        Assert.Equal(T0, failed.ExecutionStarted);
        AssertTestRunRow(failed, build, run.Id);
    }

    [Fact]
    public async Task Passed_run_has_no_failure_code_and_completes_history()
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);

        var run = Assert.Single(await TestRunsFor(build.Id));
        Assert.Equal(TestStatus.Passed, run.Status);
        Assert.Null(run.FailureCode);
        Assert.Equal("Passed", run.Result);
        Assert.Equal(T0, run.CompletedAt);

        var history = await HistoryFor(build.Id);
        Assert.Equal(2, history.Count);
        var completed = Assert.Single(history, h => h.EventType == "Completed");
        Assert.Equal("Running", completed.OldStatus);
        Assert.Equal("Passed", completed.NewStatus);
        AssertTestRunRow(completed, build, run.Id);
    }

    [Theory]
    [MemberData(nameof(MissingFailureCodes))]
    public async Task Failed_run_without_code_is_rejected_and_nothing_is_written(string? code)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Failed, Station, Operator, code);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Failure code is required for a failed test run.", result.Error);
        await AssertNothingWritten(build.Id);
    }

    [Theory]
    [MemberData(nameof(UnknownFailureCodes))]
    public async Task Failed_run_with_unknown_code_is_rejected_and_nothing_is_written(string code)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Failed, Station, Operator, code);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Unknown failure code.", result.Error);
        await AssertNothingWritten(build.Id);
    }

    [Fact]
    public async Task Passed_run_with_code_is_rejected_and_nothing_is_written()
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, FailureCodes.Leak);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Failure code is only allowed for a failed test run.", result.Error);
        await AssertNothingWritten(build.Id);
    }

    [Theory]
    [MemberData(nameof(RejectedResults))]
    public async Task Result_that_is_not_a_finished_state_is_rejected_by_transitions(TestStatus result)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var outcome = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, result, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Invalid, outcome.Outcome);
        Assert.Equal(Transitions.Validate(TestStatus.Running, result), outcome.Error);
        await AssertNothingWritten(build.Id);
    }

    [Theory]
    [MemberData(nameof(NotRunningStatuses))]
    public async Task Build_that_is_not_running_is_rejected_and_nothing_is_written(BuildStatus status)
    {
        var build = await SeedBuild(status);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Test runs can only be submitted for a running build.", result.Error);
        Assert.Equal(status, (await StoredBuild(build.Id)).Status);
        await AssertNothingWritten(build.Id);
    }

    [Fact]
    public async Task Unknown_build_returns_NotFound_and_writes_nothing()
    {
        await using var ctx = CreateContext();
        var missingId = Guid.NewGuid();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(missingId, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.NotFound, result.Outcome);
        Assert.Equal("Build not found.", result.Error);
        Assert.Null(result.Event);
        await AssertNothingWritten(missingId);
    }

    [Theory]
    [MemberData(nameof(BadStations))]
    public async Task Blank_or_oversized_station_is_rejected_and_writes_nothing(string station, string expectedError)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Failed, station, Operator, FailureCodes.Leak);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal(expectedError, result.Error);
        Assert.Null(result.Event);
        await AssertNothingWritten(build.Id);
    }

    [Theory]
    [MemberData(nameof(BadOperators))]
    public async Task Blank_or_oversized_operator_is_rejected_and_writes_nothing(string operatorName, string expectedError)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Failed, Station, operatorName, FailureCodes.Leak);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal(expectedError, result.Error);
        Assert.Null(result.Event);
        await AssertNothingWritten(build.Id);
    }

    [Fact]
    public async Task Station_at_limit_and_operator_at_limit_are_accepted()
    {
        var station = new string('S', 50);
        var operatorName = new string('o', 256);
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, station, operatorName, null);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var history = await HistoryFor(build.Id);
        Assert.Equal(2, history.Count);
        Assert.All(history, row =>
        {
            Assert.Equal(station, row.Station);
            Assert.Equal(operatorName, row.Operator);
        });
    }

    [Theory]
    [MemberData(nameof(BlankPassedCodes))]
    public async Task Passed_run_with_blank_code_is_stored_without_code(string code)
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, code);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var run = Assert.Single(await TestRunsFor(build.Id));
        Assert.Null(run.FailureCode);
        Assert.Equal("Passed", run.Result);
    }

    [Fact]
    public async Task Successful_submit_leaves_build_status_and_updated_at_unchanged()
    {
        var build = await SeedBuild(BuildStatus.Running, updatedAt: T0);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0.AddMinutes(5)))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var stored = await StoredBuild(build.Id);
        Assert.Equal(BuildStatus.Running, stored.Status);
        Assert.Equal(T0, stored.UpdatedAt);
    }

    [Fact]
    public async Task Null_workflow_instance_id_is_assigned_and_shared_by_both_rows()
    {
        var build = await SeedBuild(BuildStatus.Running, workflowInstanceId: null);
        await using var ctx = CreateContext();

        var result = await new BuildService(ctx, new FakeTime(T0))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Applied, result.Outcome);
        var assigned = (await StoredBuild(build.Id)).WorkflowInstanceId;
        Assert.NotNull(assigned);
        Assert.NotEqual(Guid.Empty, assigned.Value);

        var history = await HistoryFor(build.Id);
        Assert.Equal(2, history.Count);
        Assert.All(history, row => Assert.Equal(assigned, row.WorkflowInstanceId));
    }

    [Fact]
    public async Task Build_updated_before_guarded_update_is_rejected_and_writes_nothing()
    {
        var build = await SeedBuild(BuildStatus.Running);
        var later = T0.AddSeconds(5);
        await using var ctx = CreateContext(new ExternalChangeBeforeUpdate(sql.ConnectionString, conn =>
            ExecuteAsync(conn, "UPDATE dbo.Builds SET UpdatedAt = @at WHERE Id = @id",
                ("@at", later), ("@id", build.Id))));

        var result = await new BuildService(ctx, new FakeTime(T0.AddMinutes(1)))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Build status changed; reload and retry.", result.Error);
        Assert.Null(result.Event);
        var stored = await StoredBuild(build.Id);
        Assert.Equal(BuildStatus.Running, stored.Status);
        Assert.Equal(later, stored.UpdatedAt);
        await AssertNothingWritten(build.Id);
    }

    [Fact]
    public async Task Build_status_changed_before_guarded_update_is_rejected_and_writes_nothing()
    {
        var build = await SeedBuild(BuildStatus.Running);
        await using var ctx = CreateContext(new ExternalChangeBeforeUpdate(sql.ConnectionString, conn =>
            ExecuteAsync(conn, "UPDATE dbo.Builds SET Status = @status WHERE Id = @id",
                ("@status", (int)BuildStatus.Complete), ("@id", build.Id))));

        var result = await new BuildService(ctx, new FakeTime(T0.AddMinutes(1)))
            .SubmitTestRunAsync(build.Id, TestStatus.Passed, Station, Operator, null);

        Assert.Equal(TransitionOutcome.Invalid, result.Outcome);
        Assert.Equal("Build status changed; reload and retry.", result.Error);
        Assert.Null(result.Event);
        var stored = await StoredBuild(build.Id);
        Assert.Equal(BuildStatus.Complete, stored.Status);
        Assert.Equal(T0, stored.UpdatedAt);
        await AssertNothingWritten(build.Id);
    }

    private static void AssertTestRunRow(WorkflowHistoryRecord row, Build build, Guid testRunId)
    {
        Assert.Equal("TestRun", row.ActivityName);
        Assert.Equal(build.Id, row.BuildId);
        Assert.Equal(Station, row.Station);
        Assert.Equal(Operator, row.Operator);
        Assert.NotNull(row.StateSnapshot);
        using var snapshot = JsonDocument.Parse(row.StateSnapshot);
        Assert.Equal(testRunId, snapshot.RootElement.GetProperty("testRunId").GetGuid());
    }

    private async Task AssertNothingWritten(Guid buildId)
    {
        Assert.Empty(await TestRunsFor(buildId));
        Assert.Empty(await HistoryFor(buildId));
    }

    private FactoryDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(sql.ConnectionString);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        return new FactoryDbContext(builder.Options);
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

    private async Task<Build> StoredBuild(Guid buildId)
    {
        await using var ctx = CreateContext();
        return await ctx.Builds.AsNoTracking().SingleAsync(b => b.Id == buildId);
    }

    private async Task<List<TestRun>> TestRunsFor(Guid buildId)
    {
        await using var ctx = CreateContext();
        return await ctx.TestRuns.AsNoTracking().Where(t => t.BuildId == buildId).ToListAsync();
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
