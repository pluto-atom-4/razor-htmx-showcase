using System.Net;
using System.Text.RegularExpressions;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using FactoryApp.Web.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardTestRunTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly string Station = BoardModel.Stations[0];

    [Fact]
    public async Task Failed_run_with_code_returns_out_of_band_running_column_and_writes_rows()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, "Failed", Station, FailureCodes.Leak));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, Regex.Matches(body, "id=\"column-").Count);
        var running = HtmlAssert.Section(body, "Running");
        Assert.Contains("hx-swap-oob=\"outerHTML\"", running);
        Assert.Contains($"id=\"card-{buildId}\"", running);
        Assert.Contains("Last test: Failed (LEAK)", CardFor(body, buildId));

        var run = Assert.Single(await BoardDb.TestRunsAsync(factory, buildId));
        Assert.Equal(TestStatus.Failed, run.Status);
        Assert.Equal(FailureCodes.Leak, run.FailureCode);
        Assert.Equal("Failed: LEAK", run.Result);

        var history = (await BoardDb.HistoryAsync(factory, buildId))
            .Where(h => h.ActivityName == "TestRun")
            .ToList();
        Assert.Equal(2, history.Count);
        Assert.All(history, h =>
        {
            Assert.Equal(Station, h.Station);
            Assert.Equal(AuthTestClient.AdminEmail, h.Operator);
        });
        Assert.Equal(BuildStatus.Running, await BoardDb.StatusOfAsync(factory, buildId));
    }

    [Fact]
    public async Task Passed_run_with_blank_code_option_stores_no_code()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, "Passed", Station, ""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Last test: Passed", CardFor(body, buildId));
        var run = Assert.Single(await BoardDb.TestRunsAsync(factory, buildId));
        Assert.Equal(TestStatus.Passed, run.Status);
        Assert.Null(run.FailureCode);
    }

    [Theory]
    [InlineData("Failed", null, "Failure code is required for a failed test run.")]
    [InlineData("Failed", "", "Failure code is required for a failed test run.")]
    [InlineData("Failed", "BOGUS", "Unknown failure code.")]
    [InlineData("Passed", "LEAK", "Failure code is only allowed")]
    public async Task Invalid_failure_code_returns_422_and_writes_nothing(string result, string? code, string message)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, result, Station, code));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("class=\"error\"", body);
        Assert.Contains(message, body);
        await AssertNothingWrittenAsync(buildId, BuildStatus.Running);
    }

    [Theory]
    [InlineData("NOPE-9")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Unknown_station_returns_422_and_writes_nothing(string? station)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, "Passed", station, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Unknown station.", await response.Content.ReadAsStringAsync());
        await AssertNothingWrittenAsync(buildId, BuildStatus.Running);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("99")]
    public async Task Result_outside_passed_or_failed_returns_422_and_writes_nothing(string result)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, result, Station, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("class=\"error\"", await response.Content.ReadAsStringAsync());
        await AssertNothingWrittenAsync(buildId, BuildStatus.Running);
    }

    [Theory]
    [InlineData(BuildStatus.Pending)]
    [InlineData(BuildStatus.Complete)]
    public async Task Build_not_running_returns_422_and_writes_nothing(BuildStatus status)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, status);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, "Passed", Station, null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Test runs can only be submitted for a running build.", await response.Content.ReadAsStringAsync());
        await AssertNothingWrittenAsync(buildId, status);
    }

    [Fact]
    public async Task Unknown_build_returns_404()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, Guid.NewGuid(), "Passed", Station, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_antiforgery_token_returns_400_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(null, buildId, "Passed", Station, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingWrittenAsync(buildId, BuildStatus.Running);
    }

    [Fact]
    public async Task Anonymous_post_redirects_to_login_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var anonymous = AuthTestClient.CreateClient(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(anonymous, AuthTestClient.LoginPath);

        using var response = await anonymous.PostAsync(
            AuthTestClient.SubmitTestRunPath, BoardDb.SubmitTestRunForm(token, buildId, "Passed", Station, null));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(AuthTestClient.LoginPath, HtmlAssert.PathAndQuery(response));
        await AssertNothingWrittenAsync(buildId, BuildStatus.Running);
    }

    [Fact]
    public async Task Running_card_renders_test_run_form_with_stations_codes_and_results()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        Assert.Contains($"id=\"testrun-form-{buildId}\"", body);
        var form = TestRunFormFor(body, buildId);
        Assert.Contains($"name=\"BuildId\" value=\"{buildId}\"", form);
        Assert.Contains("hx-post=\"/Board?handler=SubmitTestRun\"", form);
        Assert.Contains($"hx-target=\"#card-error-{buildId}\"", form);

        var stations = OptionValues(form, $"<select id=\"testrun-station-{buildId}\" name=\"Station\">");
        Assert.Equal(BoardModel.Stations, stations);

        var codes = OptionValues(form, $"<select id=\"failure-code-{buildId}\" name=\"FailureCode\">");
        Assert.Equal(new[] { "" }.Concat(FailureCodes.All).ToArray(), codes);

        var results = Regex.Matches(form, "name=\"Result\" value=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.Equal(new[] { "Passed", "Failed" }, results);
    }

    [Theory]
    [InlineData(BuildStatus.Pending)]
    [InlineData(BuildStatus.Complete)]
    [InlineData(BuildStatus.Failed)]
    public async Task Non_running_cards_render_no_test_run_form(BuildStatus status)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, status);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        Assert.Contains($"id=\"card-{buildId}\"", body);
        Assert.DoesNotContain($"id=\"testrun-form-{buildId}\"", body);
    }

    [Fact]
    public async Task Running_build_with_no_test_runs_shows_no_last_test_line()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var card = CardFor(await client.GetStringAsync("/Board"), buildId);

        Assert.DoesNotContain("Last test:", card);
    }

    [Fact]
    public async Task Newest_test_run_is_the_one_shown_even_when_its_id_sorts_lower()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        var newestId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var oldestId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await AddTestRunAsync(buildId, newestId, TestStatus.Failed, "Failed: LEAK", FailureCodes.Leak,
            new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        await AddTestRunAsync(buildId, oldestId, TestStatus.Passed, "Passed", null,
            new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc));
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var card = CardFor(await client.GetStringAsync("/Board"), buildId);

        Assert.Contains("Last test: Failed (LEAK)", card);
        Assert.DoesNotContain("Last test: Passed", card);
    }

    [Fact]
    public async Task Anonymous_htmx_post_gets_401_with_hx_redirect_to_login_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = AuthTestClient.CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthTestClient.SubmitTestRunPath)
        {
            Content = BoardDb.SubmitTestRunForm(null, buildId, "Passed", Station, null),
        };
        request.Headers.Add("HX-Request", "true");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("HX-Redirect", out var values), "HX-Redirect header missing.");
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=", Assert.Single(values));
        Assert.Null(response.Headers.Location);
        await AssertNothingWrittenAsync(buildId, BuildStatus.Running);
    }

    private async Task AssertNothingWrittenAsync(Guid buildId, BuildStatus expectedStatus)
    {
        Assert.Equal(expectedStatus, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.TestRunsAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    private async Task AddTestRunAsync(
        Guid buildId, Guid id, TestStatus status, string result, string? code, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        await db.TestRuns.Where(t => t.Id == id).ExecuteDeleteAsync();
        db.TestRuns.Add(new TestRun
        {
            Id = id,
            BuildId = buildId,
            Status = status,
            Result = result,
            FailureCode = code,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    private static string CardFor(string body, Guid buildId)
    {
        var match = Regex.Match(body, $"<article id=\"card-{buildId}\"[\\s\\S]*?</article>");
        Assert.True(match.Success, $"Card {buildId} not found.");
        return match.Value;
    }

    private static string TestRunFormFor(string body, Guid buildId)
    {
        var match = Regex.Match(body, $"<form[^>]*id=\"testrun-form-{buildId}\"[\\s\\S]*?</form>");
        Assert.True(match.Success, $"Test run form for card {buildId} not found.");
        return match.Value;
    }

    private static string[] OptionValues(string form, string selectOpenTag)
    {
        var start = form.IndexOf(selectOpenTag, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Select {selectOpenTag} not found.");
        var end = form.IndexOf("</select>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Select {selectOpenTag} is not closed.");
        return Regex.Matches(form[start..end], "<option value=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
    }
}
