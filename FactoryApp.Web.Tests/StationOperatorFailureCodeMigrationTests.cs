using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.MsSql;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class StationOperatorFailureCodeMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260709234406_AddBuildWorkflowRelationship";

    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Upgrade_keeps_existing_rows_and_new_columns_round_trip()
    {
        var connectionString = _container.GetConnectionString();
        var buildId = Guid.NewGuid();
        var testRunId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var workflowInstanceId = Guid.NewGuid();
        var recordedAt = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);

        await using (var ctx = CreateContext(connectionString))
        {
            await ((IInfrastructure<IServiceProvider>)ctx).GetService<IMigrator>()
                .MigrateAsync(PreviousMigration);

            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO dbo.Builds (Id, Name, Status, CreatedAt, UpdatedAt)
                VALUES ({buildId}, 'Legacy build', 0, {recordedAt}, {recordedAt});
                """);
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO dbo.TestRuns (Id, BuildId, Status, Result, FileUrl, CreatedAt, UpdatedAt)
                VALUES ({testRunId}, {buildId}, 2, 'ok', 'https://example.test/run.log', {recordedAt}, {recordedAt});
                """);
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO dbo.WorkflowHistory
                    (Id, WorkflowInstanceId, EventType, ActivityName, OldStatus, NewStatus, RecordedAt, BuildId)
                VALUES ({historyId}, {workflowInstanceId}, 'Started', 'Build', 'Pending', 'Running', {recordedAt}, {buildId});
                """);
        }

        await using (var ctx = CreateContext(connectionString))
        {
            await ctx.Database.MigrateAsync();
        }

        await using (var ctx = CreateContext(connectionString))
        {
            var build = await ctx.Builds.SingleAsync(b => b.Id == buildId);
            var testRun = await ctx.TestRuns.SingleAsync(t => t.Id == testRunId);
            var history = await ctx.WorkflowHistory.SingleAsync(h => h.Id == historyId);

            Assert.Equal("Legacy build", build.Name);
            Assert.Equal("ok", testRun.Result);
            Assert.Equal("https://example.test/run.log", testRun.FileUrl);
            Assert.Null(testRun.FailureCode);
            Assert.Equal("Started", history.EventType);
            Assert.Equal("Running", history.NewStatus);
            Assert.Equal(workflowInstanceId, history.WorkflowInstanceId);
            Assert.Null(history.Station);
            Assert.Null(history.Operator);
        }

        var newHistoryId = Guid.NewGuid();
        var newTestRunId = Guid.NewGuid();
        const string operatorEmail = "operator@example.com";

        await using (var ctx = CreateContext(connectionString))
        {
            ctx.WorkflowHistory.Add(new WorkflowHistoryRecord
            {
                Id = newHistoryId,
                WorkflowInstanceId = workflowInstanceId,
                BuildId = buildId,
                EventType = "Completed",
                ActivityName = "Assembly",
                OldStatus = "Running",
                NewStatus = "Complete",
                RecordedAt = recordedAt,
                Station = "ASSEMBLY-1",
                Operator = operatorEmail
            });
            ctx.TestRuns.Add(new TestRun
            {
                Id = newTestRunId,
                BuildId = buildId,
                Status = TestStatus.Failed,
                FailureCode = "LEAK"
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext(connectionString))
        {
            var history = await ctx.WorkflowHistory.SingleAsync(h => h.Id == newHistoryId);
            var testRun = await ctx.TestRuns.SingleAsync(t => t.Id == newTestRunId);

            Assert.Equal("ASSEMBLY-1", history.Station);
            Assert.Equal(operatorEmail, history.Operator);
            Assert.Equal("LEAK", testRun.FailureCode);
        }

        await using (var ctx = CreateContext(connectionString))
        {
            var model = ctx.Model;
            Assert.Equal(50, model.FindEntityType(typeof(WorkflowHistoryRecord))!
                .FindProperty(nameof(WorkflowHistoryRecord.Station))!.GetMaxLength());
            Assert.Equal(256, model.FindEntityType(typeof(WorkflowHistoryRecord))!
                .FindProperty(nameof(WorkflowHistoryRecord.Operator))!.GetMaxLength());
            Assert.Equal(30, model.FindEntityType(typeof(TestRun))!
                .FindProperty(nameof(TestRun.FailureCode))!.GetMaxLength());
        }
    }

    private static FactoryDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new FactoryDbContext(options);
    }
}
