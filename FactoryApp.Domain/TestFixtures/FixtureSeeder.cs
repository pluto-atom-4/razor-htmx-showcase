using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Domain.TestFixtures;

/// <summary>
/// Programmatic seeding of development fixtures: users, builds, parts, test runs and workflow history.
/// Every seed step is idempotent: rows with fixed Ids are added only when missing.
///
/// Usage:
///   if (app.Environment.IsDevelopment())
///     await FixtureSeeder.SeedAllAsync(dbContext, isDevelopment: true);
/// </summary>
public class FixtureSeeder
{
    private static readonly DateTime Anchor = DateTime.UtcNow;

    private static readonly Dictionary<string, Guid> TestUserIds = new()
    {
        { "test@example.com", new Guid("00000000-0000-0000-0000-000000000001") },
        { "admin@example.com", new Guid("00000000-0000-0000-0000-000000000002") },
        { "user@example.com", new Guid("00000000-0000-0000-0000-000000000003") }
    };

    private static readonly Dictionary<string, Guid> TestBuildIds = new()
    {
        { "pending-01", new Guid("10000000-0000-0000-0000-000000000001") },
        { "running-01", new Guid("10000000-0000-0000-0000-000000000002") },
        { "running-02", new Guid("10000000-0000-0000-0000-000000000003") },
        { "complete-01", new Guid("10000000-0000-0000-0000-000000000004") },
        { "failed-01", new Guid("10000000-0000-0000-0000-000000000005") },
        { "dataloader-test", new Guid("10000000-0000-0000-0000-000000000006") },
        { "pagination-test", new Guid("10000000-0000-0000-0000-000000000007") },
        { "transaction-test", new Guid("10000000-0000-0000-0000-000000000008") },
        { "edge-case-01", new Guid("10000000-0000-0000-0000-000000000009") },
        { "edge-case-02", new Guid("10000000-0000-0000-0000-000000000010") },
        { "decimal-precision-test", new Guid("10000000-0000-0000-0000-000000000011") },
        { "hist-c1", new Guid("10000000-0000-0000-0000-000000000012") },
        { "hist-c2", new Guid("10000000-0000-0000-0000-000000000013") },
        { "hist-c3", new Guid("10000000-0000-0000-0000-000000000014") },
        { "hist-c4", new Guid("10000000-0000-0000-0000-000000000015") },
        { "hist-c5", new Guid("10000000-0000-0000-0000-000000000016") },
        { "hist-f1", new Guid("10000000-0000-0000-0000-000000000017") },
        { "hist-f2", new Guid("10000000-0000-0000-0000-000000000018") }
    };

    private static readonly (string Key, string Name, string Description)[] PendingBuildSpecs =
    {
        ("pending-01", "Q2 2026 Production Run", "High-volume manufacturing build for Q2 2026"),
        ("dataloader-test", "DataLoader Test Build", "Build with 10+ parts for N+1 query testing"),
        ("pagination-test", "Pagination Test Build", "Build for pagination boundary testing"),
        ("transaction-test", "Transaction Test Build", "Build for EF Core + Dapper transaction atomicity testing"),
        ("edge-case-01", "Edge Case Build 1", new string('x', 1000)),
        ("edge-case-02", "Edge Case Build 2", string.Empty),
        ("decimal-precision-test", "Decimal Precision Test Build", "Build with fractional quantity parts for precision testing (issue #132)")
    };

    // StartHoursAgo: Pending->Running. DurationMinutes: Running->terminal, null while still open.
    private static readonly (string Key, string Name, string? Description, BuildStatus Status, int StartHoursAgo, int? DurationMinutes, string Operator, string? Error)[] LifecycleBuilds =
    {
        ("running-01", "Legacy System Migration", "Update legacy system components", BuildStatus.Running, 24, null, "test@example.com", null),
        ("running-02", "Testing Phase Build", "Comprehensive test coverage phase", BuildStatus.Running, 2, null, "user@example.com", null),
        ("complete-01", "Completed Build A", "Successfully finished build", BuildStatus.Complete, 78, 360, "admin@example.com", null),
        ("failed-01", "Failed Build B", "Build failed during testing phase", BuildStatus.Failed, 32, 480, "user@example.com", "Voltage out of tolerance: 11.8V"),
        ("hist-c1", "History Build 1", null, BuildStatus.Complete, 480, 90, "test@example.com", null),
        ("hist-c2", "History Build 2", null, BuildStatus.Complete, 384, 180, "admin@example.com", null),
        ("hist-c3", "History Build 3", null, BuildStatus.Complete, 288, 270, "user@example.com", null),
        ("hist-c4", "History Build 4", null, BuildStatus.Complete, 216, 120, "test@example.com", null),
        ("hist-c5", "History Build 5", null, BuildStatus.Complete, 144, 720, "admin@example.com", null),
        ("hist-f1", "History Build 6", null, BuildStatus.Failed, 336, 45, "user@example.com", "Fixture jam"),
        ("hist-f2", "History Build 7", null, BuildStatus.Failed, 168, 360, "test@example.com", "Sealant cure failed")
    };

    // Test run N is the N-th entry (1-based); its Id is TestRunId(N). CompletedHoursAgo is null while running.
    private static readonly (string Build, TestStatus Status, int? CompletedHoursAgo, int DurationMinutes, string Station, string Operator, string? FailureCode)[] TestRunSpecs =
    {
        ("complete-01", TestStatus.Passed, 72, 25, "TEST-BAY-1", "test@example.com", null),
        ("complete-01", TestStatus.Passed, 48, 40, "TEST-BAY-2", "admin@example.com", null),
        ("failed-01", TestStatus.Failed, 24, 18, "TEST-BAY-1", "user@example.com", "SENSOR_FAULT"),
        ("failed-01", TestStatus.Running, null, 30, "TEST-BAY-2", "admin@example.com", null),
        ("hist-c1", TestStatus.Passed, 476, 20, "TEST-BAY-1", "test@example.com", null),
        ("hist-c2", TestStatus.Passed, 378, 55, "TEST-BAY-2", "admin@example.com", null),
        ("hist-c3", TestStatus.Failed, 280, 35, "TEST-BAY-1", "user@example.com", "LEAK"),
        ("hist-c3", TestStatus.Passed, 276, 30, "TEST-BAY-2", "admin@example.com", null),
        ("hist-c4", TestStatus.Passed, 212, 45, "TEST-BAY-1", "user@example.com", null),
        ("hist-c5", TestStatus.Failed, 130, 70, "TEST-BAY-2", "test@example.com", "TIMEOUT"),
        ("hist-c5", TestStatus.Passed, 126, 50, "TEST-BAY-2", "test@example.com", null),
        ("hist-f1", TestStatus.Failed, 333, 15, "TEST-BAY-1", "admin@example.com", "OVERPRESSURE"),
        ("hist-f2", TestStatus.Failed, 160, 90, "TEST-BAY-2", "user@example.com", "LEAK"),
        ("hist-f2", TestStatus.Failed, 156, 60, "TEST-BAY-1", "test@example.com", "LEAK"),
        ("hist-c2", TestStatus.Failed, 380, 25, "TEST-BAY-1", "test@example.com", "OVERPRESSURE"),
        ("hist-c1", TestStatus.Failed, 477, 30, "TEST-BAY-2", "admin@example.com", "OTHER")
    };

    private static readonly Dictionary<int, (string? Result, string? FileUrl)> LegacyTestRunResults = new()
    {
        [1] = ("All assertions passed - 150/150 tests successful", "https://example.com/test-results/build-passed.json"),
        [2] = ("Regression tests passed - no performance degradation", "https://example.com/test-results/perf-test.json"),
        [3] = ("Assertion failed at line 42: expected voltage 12.0V ± 0.1V, got 11.8V", "https://example.com/test-logs/failure.log")
    };

    /// <summary>
    /// Seed all development fixtures: users, builds, parts, test runs and workflow history.
    /// </summary>
    public static async Task SeedAllAsync(FactoryDbContext dbContext, bool isDevelopment)
    {
        RequireDevelopment(isDevelopment);

        string hashedPassword = BCrypt.Net.BCrypt.HashPassword("SecurePassword123!");

        await SeedTestUsersAsync(dbContext, hashedPassword);
        await SeedTestBuildsAsync(dbContext);
        await SeedTestPartsAsync(dbContext);
        await SeedTestRunsAsync(dbContext);
        await SeedHistoryAsync(dbContext);

        Console.WriteLine("✓ Test fixtures seeded successfully");
    }

    private static async Task SeedTestUsersAsync(FactoryDbContext dbContext, string hashedPassword)
    {
        var users = new[]
        {
            new AuthUser
            {
                Id = TestUserIds["test@example.com"],
                Email = "test@example.com",
                PasswordHash = hashedPassword
            },
            new AuthUser
            {
                Id = TestUserIds["admin@example.com"],
                Email = "admin@example.com",
                PasswordHash = hashedPassword
            },
            new AuthUser
            {
                Id = TestUserIds["user@example.com"],
                Email = "user@example.com",
                PasswordHash = hashedPassword
            }
        };

        foreach (var user in users)
        {
            var existing = await dbContext.AuthUsers.FirstOrDefaultAsync(u => u.Email == user.Email);
            if (existing == null)
            {
                dbContext.AuthUsers.Add(user);
            }
            else
            {
                existing.PasswordHash = hashedPassword;
                dbContext.AuthUsers.Update(existing);
            }
        }

        await dbContext.SaveChangesAsync();
        Console.WriteLine($"  • Test users: {users.Length}");
    }

    /// <summary>
    /// Seed pending, running, complete and failed builds, including the history builds.
    /// </summary>
    public static async Task SeedTestBuildsAsync(FactoryDbContext dbContext)
    {
        var pending = PendingBuildSpecs.Select(p => new Build
        {
            Id = TestBuildIds[p.Key],
            Name = p.Name,
            Description = p.Description,
            Status = BuildStatus.Pending,
            CreatedAt = Anchor,
            UpdatedAt = Anchor
        });

        var lifecycle = LifecycleBuilds.Select(b =>
        {
            var (start, end) = BuildWindow(b.StartHoursAgo, b.DurationMinutes);
            return new Build
            {
                Id = TestBuildIds[b.Key],
                Name = b.Name,
                Description = b.Description,
                Status = b.Status,
                CreatedAt = start.AddHours(-2),
                UpdatedAt = end ?? start
            };
        });

        var builds = pending.Concat(lifecycle).ToArray();
        foreach (var build in builds)
        {
            if (!await dbContext.Builds.AnyAsync(b => b.Id == build.Id))
            {
                dbContext.Builds.Add(build);
            }
        }

        await dbContext.SaveChangesAsync();
        Console.WriteLine($"  • Test builds: {builds.Length} (PENDING, RUNNING, COMPLETE, FAILED, edge cases, history builds)");
    }

    /// <summary>
    /// Seed test parts under the DataLoader test build and decimal precision test build.
    /// Creates 10 parts with integer quantities for DataLoader testing.
    /// Creates 3 parts with fractional quantities for decimal precision testing (issue #132).
    /// </summary>
    public static async Task SeedTestPartsAsync(FactoryDbContext dbContext)
    {
        // DataLoader test parts (integer quantities)
        var dataloaderBuildId = TestBuildIds["dataloader-test"];
        var dataloaderParts = new Part[]
        {
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Precision Bearing", SKU = "SKU-PB-001", Quantity = 5, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Control Module", SKU = "SKU-CM-042", Quantity = 2, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Power Supply Unit", SKU = "SKU-PSU-003", Quantity = 1, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Connector Assembly", SKU = "SKU-CA-015", Quantity = 8, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Sensor Module", SKU = "SKU-SM-007", Quantity = 3, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Communication Board", SKU = "SKU-CB-021", Quantity = 1, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Thermal Interface", SKU = "SKU-TI-009", Quantity = 4, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Mounting Hardware", SKU = "SKU-MH-012", Quantity = 50, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Cable Assembly A", SKU = "SKU-CAB-A-001", Quantity = 2, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = dataloaderBuildId, Name = "Cable Assembly B", SKU = "SKU-CAB-B-002", Quantity = 3, CreatedAt = DateTime.UtcNow }
        };

        foreach (var part in dataloaderParts)
        {
            if (!await dbContext.Parts.AnyAsync(p => p.BuildId == dataloaderBuildId && p.SKU == part.SKU))
            {
                dbContext.Parts.Add(part);
            }
        }

        // Decimal precision test parts (fractional quantities for issue #132)
        var precisionBuildId = TestBuildIds["decimal-precision-test"];
        var precisionParts = new Part[]
        {
            new() { Id = Guid.NewGuid(), BuildId = precisionBuildId, Name = "Half Unit Component", SKU = "SKU-HALF-001", Quantity = 0.5m, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = precisionBuildId, Name = "Single Point Five Units", SKU = "SKU-1.5-002", Quantity = 1.5m, CreatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), BuildId = precisionBuildId, Name = "Two Point Seventy Five Units", SKU = "SKU-2.75-003", Quantity = 2.75m, CreatedAt = DateTime.UtcNow }
        };

        foreach (var part in precisionParts)
        {
            if (!await dbContext.Parts.AnyAsync(p => p.BuildId == precisionBuildId && p.SKU == part.SKU))
            {
                dbContext.Parts.Add(part);
            }
        }

        await dbContext.SaveChangesAsync();
        Console.WriteLine("  • Test parts: 13 (10 integer + 3 decimal precision)");
    }

    /// <summary>
    /// Seed test runs covering PASSED, FAILED and RUNNING statuses across the history builds.
    /// </summary>
    public static async Task SeedTestRunsAsync(FactoryDbContext dbContext)
    {
        var testRuns = TestRunSpecs.Select((spec, index) =>
        {
            var number = index + 1;
            var (start, end) = TestRunWindow(spec.CompletedHoursAgo, spec.DurationMinutes);
            LegacyTestRunResults.TryGetValue(number, out var legacy);
            return new TestRun
            {
                Id = TestRunId(number),
                BuildId = TestBuildIds[spec.Build],
                Status = spec.Status,
                Result = legacy.Result ?? (spec.Status switch
                {
                    TestStatus.Passed => "Passed",
                    TestStatus.Failed => $"Failed: {spec.FailureCode}",
                    _ => null
                }),
                FileUrl = legacy.FileUrl,
                FailureCode = spec.FailureCode,
                CompletedAt = end,
                CreatedAt = start,
                UpdatedAt = end ?? start
            };
        }).ToArray();

        foreach (var testRun in testRuns)
        {
            if (!await dbContext.TestRuns.AnyAsync(t => t.Id == testRun.Id))
            {
                dbContext.TestRuns.Add(testRun);
            }
        }

        await dbContext.SaveChangesAsync();
        Console.WriteLine($"  • Test runs: {testRuns.Length} (PASSED, FAILED, RUNNING statuses)");
    }

    /// <summary>
    /// Seed workflow history for the lifecycle builds and their test runs.
    /// </summary>
    public static async Task SeedHistoryAsync(FactoryDbContext dbContext)
    {
        var buildIds = LifecycleBuilds.Select(b => TestBuildIds[b.Key]).ToList();
        var builds = await dbContext.Builds
            .Where(b => buildIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id);
        var existingIds = new HashSet<Guid>(await dbContext.WorkflowHistory
            .Where(h => h.BuildId.HasValue && buildIds.Contains(h.BuildId.Value))
            .Select(h => h.Id)
            .ToListAsync());

        var rows = new List<WorkflowHistoryRecord>();

        foreach (var b in LifecycleBuilds)
        {
            var build = builds[TestBuildIds[b.Key]];
            build.WorkflowInstanceId ??= HistoryId($"wf:{b.Key}");
            var (start, end) = BuildWindow(b.StartHoursAgo, b.DurationMinutes);
            rows.AddRange(ExpandHistory(
                $"hist:build:{b.Key}", "Build", build.Id, build.WorkflowInstanceId.Value,
                "ASSEMBLY-1", b.Operator, start, end, b.Status.ToString(), b.Error));
        }

        foreach (var (spec, number) in TestRunSpecs.Select((s, i) => (s, i + 1)))
        {
            var build = builds[TestBuildIds[spec.Build]];
            var (start, end) = TestRunWindow(spec.CompletedHoursAgo, spec.DurationMinutes);
            var snapshot = JsonSerializer.Serialize(new { testRunId = TestRunId(number) });
            rows.AddRange(ExpandHistory(
                $"hist:testrun:{number:D2}", "TestRun", build.Id, build.WorkflowInstanceId!.Value,
                spec.Station, spec.Operator, start, end, spec.Status.ToString(), snapshot: snapshot));
        }

        var missing = rows.Where(r => !existingIds.Contains(r.Id)).ToList();
        dbContext.WorkflowHistory.AddRange(missing);

        await dbContext.SaveChangesAsync();
        Console.WriteLine($"  • Workflow history: {rows.Count} rows ({missing.Count} new)");
    }

    /// <summary>
    /// Clean up all test fixtures, including workflow history, for development reset.
    /// </summary>
    public static async Task CleanupAllAsync(FactoryDbContext dbContext, bool isDevelopment)
    {
        RequireDevelopment(isDevelopment);

        var testBuildIds = TestBuildIds.Values.ToList();
        var testUserEmails = TestUserIds.Keys.ToList();

        // WorkflowHistory has no cascade from Builds, so it is deleted before them.
        await dbContext.WorkflowHistory
            .Where(h => h.BuildId.HasValue && testBuildIds.Contains(h.BuildId.Value))
            .ExecuteDeleteAsync();
        await dbContext.TestRuns.Where(t => testBuildIds.Contains(t.BuildId)).ExecuteDeleteAsync();
        await dbContext.Parts.Where(p => testBuildIds.Contains(p.BuildId)).ExecuteDeleteAsync();
        await dbContext.Builds.Where(b => testBuildIds.Contains(b.Id)).ExecuteDeleteAsync();
        await dbContext.AuthUsers.Where(u => testUserEmails.Contains(u.Email)).ExecuteDeleteAsync();

        Console.WriteLine("✓ Test fixtures cleaned up");
    }

    // Seeded users share a known password; never run outside development.
    private static void RequireDevelopment(bool isDevelopment)
    {
        if (!isDevelopment)
        {
            throw new InvalidOperationException("Fixture seeding is development-only.");
        }
    }

    private static (DateTime Start, DateTime? End) BuildWindow(int startHoursAgo, int? durationMinutes)
    {
        var start = Anchor.AddHours(-startHoursAgo);
        DateTime? end = durationMinutes.HasValue ? start.AddMinutes(durationMinutes.Value) : null;
        return (start, end);
    }

    private static (DateTime Start, DateTime? End) TestRunWindow(int? completedHoursAgo, int durationMinutes)
    {
        DateTime? end = completedHoursAgo.HasValue ? Anchor.AddHours(-completedHoursAgo.Value) : null;
        return ((end ?? Anchor).AddMinutes(-durationMinutes), end);
    }

    // Stable across runs so re-seeding finds the history rows it already wrote.
    private static Guid HistoryId(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    private static Guid TestRunId(int number) => new($"30000000-0000-0000-0000-0000000000{number:D2}");

    private static IEnumerable<WorkflowHistoryRecord> ExpandHistory(
        string keyPrefix, string activity, Guid buildId, Guid workflowInstanceId,
        string station, string operatorEmail, DateTime start, DateTime? end, string terminalStatus,
        string? error = null, string? snapshot = null)
    {
        yield return new WorkflowHistoryRecord
        {
            Id = HistoryId($"{keyPrefix}:started"),
            WorkflowInstanceId = workflowInstanceId,
            BuildId = buildId,
            ActivityName = activity,
            EventType = "Started",
            OldStatus = "Pending",
            NewStatus = "Running",
            StateSnapshot = snapshot,
            RecordedAt = start,
            ExecutionStarted = start,
            ExecutionCompleted = start,
            Station = station,
            Operator = operatorEmail
        };

        if (end is not DateTime completedAt)
        {
            yield break;
        }

        yield return new WorkflowHistoryRecord
        {
            Id = HistoryId($"{keyPrefix}:end"),
            WorkflowInstanceId = workflowInstanceId,
            BuildId = buildId,
            ActivityName = activity,
            EventType = terminalStatus == "Failed" ? "Failed" : "Completed",
            OldStatus = "Running",
            NewStatus = terminalStatus,
            StateSnapshot = snapshot,
            ErrorMessage = error,
            RecordedAt = completedAt,
            ExecutionStarted = start,
            ExecutionCompleted = completedAt,
            ElapsedMilliseconds = (long)(completedAt - start).TotalMilliseconds,
            Station = station,
            Operator = operatorEmail
        };
    }
}
