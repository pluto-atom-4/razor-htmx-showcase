using FactoryApp.Domain;
using FactoryApp.Domain.Entities;

namespace FactoryApp.Web.Tests;

public class TransitionsTests
{
    private static readonly HashSet<(BuildStatus, BuildStatus)> AllowedBuild =
    [
        (BuildStatus.Pending, BuildStatus.Running),
        (BuildStatus.Running, BuildStatus.Complete),
        (BuildStatus.Running, BuildStatus.Failed),
        (BuildStatus.Failed, BuildStatus.Pending),
    ];

    private static readonly HashSet<(TestStatus, TestStatus)> AllowedTest =
    [
        (TestStatus.Pending, TestStatus.Running),
        (TestStatus.Running, TestStatus.Passed),
        (TestStatus.Running, TestStatus.Failed),
    ];

    public static IEnumerable<object[]> AllBuildPairs() =>
        from f in Enum.GetValues<BuildStatus>()
        from t in Enum.GetValues<BuildStatus>()
        select new object[] { f, t, AllowedBuild.Contains((f, t)) };

    public static IEnumerable<object[]> AllTestPairs() =>
        from f in Enum.GetValues<TestStatus>()
        from t in Enum.GetValues<TestStatus>()
        select new object[] { f, t, AllowedTest.Contains((f, t)) };

    [Theory]
    [MemberData(nameof(AllBuildPairs))]
    public void Build_pairs_match_allowed_table(BuildStatus from, BuildStatus to, bool expected)
    {
        Assert.Equal(expected, Transitions.IsAllowed(from, to));

        var message = Transitions.Validate(from, to);
        if (expected)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.NotNull(message);
            Assert.Contains(from.ToString(), message);
            Assert.Contains(to.ToString(), message);
        }
    }

    [Theory]
    [MemberData(nameof(AllTestPairs))]
    public void TestRun_pairs_match_allowed_table(TestStatus from, TestStatus to, bool expected)
    {
        Assert.Equal(expected, Transitions.IsAllowed(from, to));

        var message = Transitions.Validate(from, to);
        if (expected)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.NotNull(message);
            Assert.Contains(from.ToString(), message);
            Assert.Contains(to.ToString(), message);
        }
    }

    [Fact]
    public void Build_undefined_values_are_rejected_without_throwing()
    {
        var undefined = (BuildStatus)99;

        Assert.False(Transitions.IsAllowed(undefined, BuildStatus.Running));
        Assert.False(Transitions.IsAllowed(BuildStatus.Pending, undefined));
        Assert.NotNull(Transitions.Validate(undefined, BuildStatus.Running));
        Assert.NotNull(Transitions.Validate(BuildStatus.Pending, undefined));
    }

    [Fact]
    public void TestRun_undefined_values_are_rejected_without_throwing()
    {
        var undefined = (TestStatus)99;

        Assert.False(Transitions.IsAllowed(undefined, TestStatus.Running));
        Assert.False(Transitions.IsAllowed(TestStatus.Pending, undefined));
        Assert.NotNull(Transitions.Validate(undefined, TestStatus.Running));
        Assert.NotNull(Transitions.Validate(TestStatus.Pending, undefined));
    }
}
