using FactoryApp.Domain.Entities;

namespace FactoryApp.Domain;

public static class Transitions
{
    // Failed -> Pending is the retry path (owner decision D3).
    private static readonly IReadOnlyDictionary<BuildStatus, BuildStatus[]> BuildTransitions =
        new Dictionary<BuildStatus, BuildStatus[]>
        {
            [BuildStatus.Pending] = [BuildStatus.Running],
            [BuildStatus.Running] = [BuildStatus.Complete, BuildStatus.Failed],
            [BuildStatus.Failed] = [BuildStatus.Pending],
        };

    private static readonly IReadOnlyDictionary<TestStatus, TestStatus[]> TestTransitions =
        new Dictionary<TestStatus, TestStatus[]>
        {
            [TestStatus.Pending] = [TestStatus.Running],
            [TestStatus.Running] = [TestStatus.Passed, TestStatus.Failed],
        };

    public static bool IsAllowed(BuildStatus from, BuildStatus to) =>
        BuildTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsAllowed(TestStatus from, TestStatus to) =>
        TestTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static string? Validate(BuildStatus from, BuildStatus to) =>
        IsAllowed(from, to) ? null : $"Cannot move build from {from} to {to}.";

    public static string? Validate(TestStatus from, TestStatus to) =>
        IsAllowed(from, to) ? null : $"Cannot move test run from {from} to {to}.";
}
