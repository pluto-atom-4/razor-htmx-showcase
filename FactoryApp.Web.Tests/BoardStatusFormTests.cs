using System.Net;
using System.Text.RegularExpressions;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using FactoryApp.Web.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardStatusFormTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly Regex CardArticle = new("<article id=\"card-([0-9a-fA-F-]{36})\"", RegexOptions.Compiled);

    [Fact]
    public async Task Pending_card_offers_only_the_running_button()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        Assert.Equal(new[] { "Running" }, ButtonValues(form));
    }

    [Fact]
    public async Task Running_card_offers_complete_and_failed_buttons()
    {
        var buildId = await AddBuildAsync(BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        Assert.Equal(new[] { "Complete", "Failed" }, ButtonValues(form));
    }

    [Fact]
    public async Task Complete_card_renders_no_form()
    {
        var buildId = await AddBuildAsync(BuildStatus.Complete);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        Assert.Contains($"id=\"card-{buildId}\"", body);
        Assert.DoesNotContain($"id=\"form-{buildId}\"", body);
    }

    [Fact]
    public async Task Every_card_has_an_error_slot()
    {
        await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        var ids = CardArticle.Matches(body).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            Assert.Contains($"id=\"card-error-{id}\"", body);
        }
    }

    [Fact]
    public async Task Form_has_hidden_build_id_three_stations_htmx_attributes_and_token()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        Assert.Contains($"name=\"BuildId\" value=\"{buildId}\"", form);
        Assert.Contains("hx-post=\"/Board?handler=ChangeStatus\"", form);
        Assert.Contains($"hx-target=\"#card-error-{buildId}\"", form);
        Assert.Contains("name=\"__RequestVerificationToken\"", form);

        var select = Regex.Match(form, $"<select id=\"station-{buildId}\" name=\"Station\">([\\s\\S]*?)</select>");
        Assert.True(select.Success, "Station select not found.");
        var options = Regex.Matches(select.Groups[1].Value, "<option value=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(BoardModel.Stations, options);
    }

    [Fact]
    public async Task Scraped_form_fields_post_to_the_handler_and_move_the_card()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        var action = WebUtility.HtmlDecode(Regex.Match(form, "hx-post=\"([^\"]+)\"").Groups[1].Value);
        var token = WebUtility.HtmlDecode(
            Regex.Match(form, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var scrapedBuildId = Regex.Match(form, "name=\"BuildId\" value=\"([^\"]+)\"").Groups[1].Value;
        var station = Regex.Match(form, "<option value=\"([^\"]+)\"").Groups[1].Value;
        var running = Regex.Match(form, "name=\"NewStatus\" value=\"(Running)\"");
        Assert.True(running.Success, "Running button not found.");

        var fields = new Dictionary<string, string>
        {
            ["BuildId"] = scrapedBuildId,
            ["NewStatus"] = running.Groups[1].Value,
            ["Station"] = station,
            ["__RequestVerificationToken"] = token,
        };
        using var response = await client.PostAsync(action, new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"id=\"card-{buildId}\"", Section(body, "Running"));
        Assert.Equal(BuildStatus.Running, await StatusOfAsync(buildId));
    }

    [Fact]
    public async Task App_script_sets_the_response_handling_table()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var body = await client.GetStringAsync("/app.js");

        Assert.Contains("responseHandling", body);
        Assert.Contains("\"422\"", body);
    }

    private async Task<Guid> AddBuildAsync(BuildStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var build = new Build { Id = Guid.NewGuid(), Name = $"S1-11b build {Guid.NewGuid():N}", Status = status };
        db.Builds.Add(build);
        await db.SaveChangesAsync();
        return build.Id;
    }

    private async Task<BuildStatus> StatusOfAsync(Guid buildId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        return await db.Builds.AsNoTracking()
            .Where(b => b.Id == buildId)
            .Select(b => b.Status)
            .SingleAsync();
    }

    private static string FormFor(string body, Guid buildId)
    {
        var match = Regex.Match(body, $"<form[^>]*id=\"form-{buildId}\"[\\s\\S]*?</form>");
        Assert.True(match.Success, $"Form for card {buildId} not found.");
        return match.Value;
    }

    private static string[] ButtonValues(string form) =>
        Regex.Matches(form, "name=\"NewStatus\" value=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();

    private static string Section(string body, string status)
    {
        var start = body.IndexOf($"id=\"column-{status}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Column {status} not found.");
        var end = body.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Column {status} is not closed.");
        return body[start..end];
    }
}
