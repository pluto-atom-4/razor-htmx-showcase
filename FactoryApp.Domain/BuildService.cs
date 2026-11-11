using System.Text.Json;
using FactoryApp.Domain.Entities;
using FactoryApp.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Domain;

public sealed record CreateBuildResult(TransitionOutcome Outcome, string? Error = null, Guid? BuildId = null);

public sealed class BuildService(FactoryDbContext db, TimeProvider? time = null)
{
    // Must match the HasMaxLength values for Build.Name and Build.Description in FactoryDbContext.
    private const int MaxBuildNameLength = 256;
    private const int MaxDescriptionLength = 1000;
    private const int MaxStationLength = 50;
    private const int MaxOperatorLength = 256;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<CreateBuildResult> CreateBuildAsync(string name, string? description = null, CancellationToken ct = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0 || trimmedName.Length > MaxBuildNameLength)
        {
            return new CreateBuildResult(TransitionOutcome.Invalid,
                $"Name is required and must be at most {MaxBuildNameLength} characters.");
        }

        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (description is not null && description.Length > MaxDescriptionLength)
        {
            return new CreateBuildResult(TransitionOutcome.Invalid,
                $"Description must be at most {MaxDescriptionLength} characters.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var build = new Build
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Description = description,
            Status = BuildStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            WorkflowInstanceId = Guid.NewGuid(),
        };

        db.Builds.Add(build);
        await db.SaveChangesAsync(ct);

        return new CreateBuildResult(TransitionOutcome.Applied, null, build.Id);
    }

    public async Task<TransitionResult> ChangeStatusAsync(
        Guid buildId, BuildStatus newStatus, string station, string operatorName, CancellationToken ct = default)
    {
        if (ActorError(station, operatorName) is { } actorError)
        {
            return Invalid(actorError);
        }

        var build = await db.Builds.AsNoTracking().SingleOrDefaultAsync(b => b.Id == buildId, ct);
        if (build is null)
        {
            return new TransitionResult(TransitionOutcome.NotFound, "Build not found.");
        }

        var transitionError = Transitions.Validate(build.Status, newStatus);
        if (transitionError is not null)
        {
            return Invalid(transitionError);
        }

        var workflowInstanceId = build.WorkflowInstanceId is { } existing && existing != Guid.Empty
            ? existing
            : Guid.NewGuid();

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token => await ApplyAsync(build, buildId, newStatus, station, operatorName, workflowInstanceId, token),
            ct);
    }

    private async Task<TransitionResult> ApplyAsync(
        Build build, Guid buildId, BuildStatus newStatus, string station, string operatorName,
        Guid workflowInstanceId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var updated = await db.Builds
            .Where(b => b.Id == buildId && b.Status == build.Status && b.UpdatedAt == build.UpdatedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, newStatus)
                .SetProperty(b => b.UpdatedAt, now)
                .SetProperty(b => b.WorkflowInstanceId, workflowInstanceId), ct);

        if (updated == 0)
        {
            await transaction.RollbackAsync(ct);
            return Invalid("Build status changed; reload and retry.");
        }

        var leavingRunning = build.Status == BuildStatus.Running;
        var elapsedMs = leavingRunning
            ? Math.Max(0L, (long)(now - build.UpdatedAt).TotalMilliseconds)
            : (long?)null;

        db.WorkflowHistory.Add(new WorkflowHistoryRecord
        {
            Id = Guid.NewGuid(),
            WorkflowInstanceId = workflowInstanceId,
            BuildId = buildId,
            EventType = EventTypeFor(build.Status, newStatus),
            ActivityName = "Build",
            OldStatus = build.Status.ToString(),
            NewStatus = newStatus.ToString(),
            Station = station,
            Operator = operatorName,
            RecordedAt = now,
            ExecutionStarted = leavingRunning ? build.UpdatedAt : now,
            ExecutionCompleted = now,
            ElapsedMilliseconds = elapsedMs,
            ErrorMessage = null,
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Publish only after commit; a rollback must not announce a change.
        var changed = new BuildStatusChangedEvent
        {
            BuildId = buildId,
            OldStatus = build.Status,
            NewStatus = newStatus,
            Timestamp = now,
        };
        return new TransitionResult(TransitionOutcome.Applied, null, changed);
    }

    public async Task<TransitionResult> SubmitTestRunAsync(
        Guid buildId, TestStatus result, string station, string operatorName, string? failureCode,
        CancellationToken ct = default)
    {
        if (ActorError(station, operatorName) is { } actorError)
        {
            return Invalid(actorError);
        }

        if (Transitions.Validate(TestStatus.Running, result) is { } transitionError)
        {
            return Invalid(transitionError);
        }

        if (result == TestStatus.Failed)
        {
            if (string.IsNullOrWhiteSpace(failureCode))
            {
                return Invalid("Failure code is required for a failed test run.");
            }

            if (!FailureCodes.IsValid(failureCode))
            {
                return Invalid("Unknown failure code.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(failureCode))
        {
            return Invalid("Failure code is only allowed for a failed test run.");
        }

        var build = await db.Builds.AsNoTracking().SingleOrDefaultAsync(b => b.Id == buildId, ct);
        if (build is null)
        {
            return new TransitionResult(TransitionOutcome.NotFound, "Build not found.");
        }

        if (build.Status != BuildStatus.Running)
        {
            return Invalid("Test runs can only be submitted for a running build.");
        }

        var workflowInstanceId = build.WorkflowInstanceId is { } existing && existing != Guid.Empty
            ? existing
            : Guid.NewGuid();

        var code = result == TestStatus.Failed ? failureCode : null;

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token => await ApplyTestRunAsync(build, buildId, result, station, operatorName, code, workflowInstanceId, token),
            ct);
    }

    private async Task<TransitionResult> ApplyTestRunAsync(
        Build build, Guid buildId, TestStatus result, string station, string operatorName, string? failureCode,
        Guid workflowInstanceId, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // UpdatedAt must not move: ApplyAsync reads it as the Running start for elapsed time.
        var updated = await db.Builds
            .Where(b => b.Id == buildId && b.Status == BuildStatus.Running && b.UpdatedAt == build.UpdatedAt)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.WorkflowInstanceId, workflowInstanceId), ct);

        if (updated == 0)
        {
            await transaction.RollbackAsync(ct);
            return Invalid("Build status changed; reload and retry.");
        }

        var testRunId = Guid.NewGuid();
        db.TestRuns.Add(new TestRun
        {
            Id = testRunId,
            BuildId = buildId,
            Status = result,
            Result = result == TestStatus.Passed ? "Passed" : $"Failed: {failureCode}",
            FailureCode = failureCode,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var snapshot = JsonSerializer.Serialize(new { testRunId });
        db.WorkflowHistory.Add(new WorkflowHistoryRecord
        {
            Id = Guid.NewGuid(),
            WorkflowInstanceId = workflowInstanceId,
            BuildId = buildId,
            EventType = "Started",
            ActivityName = "TestRun",
            OldStatus = TestStatus.Pending.ToString(),
            NewStatus = TestStatus.Running.ToString(),
            StateSnapshot = snapshot,
            Station = station,
            Operator = operatorName,
            RecordedAt = now,
            ExecutionStarted = now,
            ExecutionCompleted = now,
            ElapsedMilliseconds = null,
        });

        // Submitted runs are recorded already finished, so the start and end share one timestamp.
        db.WorkflowHistory.Add(new WorkflowHistoryRecord
        {
            Id = Guid.NewGuid(),
            WorkflowInstanceId = workflowInstanceId,
            BuildId = buildId,
            EventType = result == TestStatus.Passed ? "Completed" : "Failed",
            ActivityName = "TestRun",
            OldStatus = TestStatus.Running.ToString(),
            NewStatus = result.ToString(),
            StateSnapshot = snapshot,
            Station = station,
            Operator = operatorName,
            RecordedAt = now,
            ExecutionStarted = now,
            ExecutionCompleted = now,
            ElapsedMilliseconds = 0,
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new TransitionResult(TransitionOutcome.Applied);
    }

    private static string? ActorError(string station, string operatorName)
    {
        if (string.IsNullOrWhiteSpace(station) || station.Length > MaxStationLength)
        {
            return $"Station is required and must be at most {MaxStationLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(operatorName) || operatorName.Length > MaxOperatorLength)
        {
            return $"Operator is required and must be at most {MaxOperatorLength} characters.";
        }

        return null;
    }

    private static string EventTypeFor(BuildStatus from, BuildStatus to) => (from, to) switch
    {
        (BuildStatus.Pending, BuildStatus.Running) => "Started",
        (BuildStatus.Running, BuildStatus.Complete) => "Completed",
        (BuildStatus.Running, BuildStatus.Failed) => "Failed",
        (BuildStatus.Failed, BuildStatus.Pending) => "Retried",
        _ => throw new ArgumentOutOfRangeException(nameof(to), $"No event type for {from} -> {to}."),
    };

    private static TransitionResult Invalid(string message) =>
        new(TransitionOutcome.Invalid, message);
}
