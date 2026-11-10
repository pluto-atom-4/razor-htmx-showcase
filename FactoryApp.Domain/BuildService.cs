using FactoryApp.Domain.Entities;
using FactoryApp.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Domain;

public sealed class BuildService(FactoryDbContext db, TimeProvider? time = null)
{
    private const int MaxStationLength = 50;
    private const int MaxOperatorLength = 256;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<TransitionResult> ChangeStatusAsync(
        Guid buildId, BuildStatus newStatus, string station, string operatorName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(station) || station.Length > MaxStationLength)
        {
            return Invalid($"Station is required and must be at most {MaxStationLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(operatorName) || operatorName.Length > MaxOperatorLength)
        {
            return Invalid($"Operator is required and must be at most {MaxOperatorLength} characters.");
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
