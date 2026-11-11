using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardPageMutationTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    [Fact]
    public async Task Empty_column_renders_placeholder()
    {
        await RemoveFailedBuildsAsync();
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        var failed = HtmlAssert.Section(body, "Failed");
        Assert.Contains("No builds", failed);
        Assert.Contains("class=\"empty\"", failed);
    }

    [Fact]
    public async Task Build_name_is_html_encoded()
    {
        await BoardDb.AddBuildAsync(factory, BuildStatus.Pending, "<b>x</b> & y");
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        Assert.Contains("&lt;b&gt;x&lt;/b&gt; &amp; y", body);
        Assert.DoesNotContain("<b>x</b>", body);
    }

    // WorkflowHistory has no cascade from Builds, so it is deleted before them.
    private async Task RemoveFailedBuildsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var failedIds = await db.Builds
            .Where(b => b.Status == BuildStatus.Failed)
            .Select(b => b.Id)
            .ToListAsync();

        await db.WorkflowHistory
            .Where(h => h.BuildId.HasValue && failedIds.Contains(h.BuildId.Value))
            .ExecuteDeleteAsync();
        await db.TestRuns.Where(t => failedIds.Contains(t.BuildId)).ExecuteDeleteAsync();
        await db.Parts.Where(p => failedIds.Contains(p.BuildId)).ExecuteDeleteAsync();
        await db.Builds.Where(b => failedIds.Contains(b.Id)).ExecuteDeleteAsync();
    }
}
