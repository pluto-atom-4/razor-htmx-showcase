using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryApp.Web.Tests;

internal static class BoardDb
{
    public static async Task<Guid> AddBuildAsync(WebAppFactory factory, BuildStatus status, string? name = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var build = new Build
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"Test build {Guid.NewGuid():N}",
            Status = status,
        };
        db.Builds.Add(build);
        await db.SaveChangesAsync();
        return build.Id;
    }

    public static async Task<BuildStatus> StatusOfAsync(WebAppFactory factory, Guid buildId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        return await db.Builds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => b.Status)
            .SingleAsync();
    }

    public static async Task<List<WorkflowHistoryRecord>> HistoryAsync(WebAppFactory factory, Guid buildId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        return await db.WorkflowHistory.AsNoTracking()
            .Where(h => h.BuildId == buildId)
            .ToListAsync();
    }

    public static FormUrlEncodedContent MoveForm(string? token, Guid buildId, string newStatus, string? station)
    {
        var form = new Dictionary<string, string>
        {
            ["BuildId"] = buildId.ToString(),
            ["NewStatus"] = newStatus,
        };
        if (station is not null)
        {
            form["Station"] = station;
        }
        if (token is not null)
        {
            form["__RequestVerificationToken"] = token;
        }

        return new FormUrlEncodedContent(form);
    }
}
