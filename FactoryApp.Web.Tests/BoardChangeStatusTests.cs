using System.Net;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardChangeStatusTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string ChangeStatusPath = "/Board?handler=ChangeStatus";
    private const string Station = "ASSEMBLY-1";

    [Fact]
    public async Task Valid_move_returns_out_of_band_columns_and_writes_one_history_row()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(ChangeStatusPath, Form(token, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var pending = Section(body, "Pending");
        var running = Section(body, "Running");
        Assert.Contains("hx-swap-oob=\"outerHTML\"", pending);
        Assert.Contains("hx-swap-oob=\"outerHTML\"", running);
        Assert.DoesNotContain($"id=\"card-{buildId}\"", pending);
        Assert.Contains($"id=\"card-{buildId}\"", running);

        Assert.Equal(BuildStatus.Running, await StatusOfAsync(buildId));
        var row = Assert.Single(await HistoryAsync(buildId));
        Assert.Equal(Station, row.Station);
        Assert.Equal(AuthTestClient.AdminEmail, row.Operator);
        Assert.Equal("Started", row.EventType);
    }

    [Fact]
    public async Task Rejected_move_returns_422_fragment_and_writes_nothing()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(ChangeStatusPath, Form(token, buildId, "Complete", Station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("class=\"error\"", body);
        Assert.Contains("Cannot move build from Pending to Complete.", body);
        Assert.Equal(BuildStatus.Pending, await StatusOfAsync(buildId));
        Assert.Empty(await HistoryAsync(buildId));
    }

    [Theory]
    [InlineData("NOPE-9")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Station_outside_the_list_returns_422_and_writes_nothing(string station)
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(ChangeStatusPath, Form(token, buildId, "Running", station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Unknown station.", await response.Content.ReadAsStringAsync());
        Assert.Equal(BuildStatus.Pending, await StatusOfAsync(buildId));
        Assert.Empty(await HistoryAsync(buildId));
    }

    [Fact]
    public async Task Undefined_status_returns_422_and_writes_nothing()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(ChangeStatusPath, Form(token, buildId, "99", Station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Invalid request.", await response.Content.ReadAsStringAsync());
        Assert.Equal(BuildStatus.Pending, await StatusOfAsync(buildId));
        Assert.Empty(await HistoryAsync(buildId));
    }

    [Fact]
    public async Task Unknown_build_returns_404()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            ChangeStatusPath, Form(token, Guid.NewGuid(), "Running", Station));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_antiforgery_token_returns_400_and_writes_nothing()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(ChangeStatusPath, Form(null, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BuildStatus.Pending, await StatusOfAsync(buildId));
        Assert.Empty(await HistoryAsync(buildId));
    }

    [Fact]
    public async Task Antiforgery_token_in_header_is_accepted()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var request = new HttpRequestMessage(HttpMethod.Post, ChangeStatusPath)
        {
            Content = Form(null, buildId, "Running", Station),
        };
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BuildStatus.Running, await StatusOfAsync(buildId));
    }

    [Fact]
    public async Task Anonymous_post_redirects_to_login_and_writes_nothing()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var anonymous = AuthTestClient.CreateClient(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(anonymous, "/Login");

        using var response = await anonymous.PostAsync(ChangeStatusPath, Form(token, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", PathAndQuery(response));
        Assert.Equal(BuildStatus.Pending, await StatusOfAsync(buildId));
        Assert.Empty(await HistoryAsync(buildId));
    }

    [Fact]
    public async Task Second_move_from_running_writes_a_completed_row()
    {
        var buildId = await AddBuildAsync(BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var first = await client.PostAsync(ChangeStatusPath, Form(token, buildId, "Running", Station));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await client.PostAsync(ChangeStatusPath, Form(token, buildId, "Complete", Station));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(BuildStatus.Complete, await StatusOfAsync(buildId));
        var history = await HistoryAsync(buildId);
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { "Completed", "Started" }, history.Select(h => h.EventType).Order().ToArray());
    }

    [Fact]
    public async Task Get_board_still_renders_plain_columns_without_out_of_band_markup()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        using var response = await client.GetAsync("/Board");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"column-Pending\"", body);
        Assert.DoesNotContain("hx-swap-oob", body);
    }

    private async Task<Guid> AddBuildAsync(BuildStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        var build = new Build { Id = Guid.NewGuid(), Name = $"S1-11a build {Guid.NewGuid():N}", Status = status };
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

    private async Task<List<WorkflowHistoryRecord>> HistoryAsync(Guid buildId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
        return await db.WorkflowHistory.AsNoTracking()
            .Where(h => h.BuildId == buildId)
            .ToListAsync();
    }

    private static FormUrlEncodedContent Form(string? token, Guid buildId, string newStatus, string station)
    {
        var form = new Dictionary<string, string>
        {
            ["BuildId"] = buildId.ToString(),
            ["NewStatus"] = newStatus,
            ["Station"] = station,
        };
        if (token is not null)
        {
            form["__RequestVerificationToken"] = token;
        }

        return new FormUrlEncodedContent(form);
    }

    private static string Section(string body, string status)
    {
        var start = body.IndexOf($"id=\"column-{status}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Column {status} not found.");
        var end = body.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Column {status} is not closed.");
        return body[start..end];
    }

    private static string? PathAndQuery(HttpResponseMessage response) =>
        response.Headers.Location is { IsAbsoluteUri: true } absolute
            ? absolute.PathAndQuery
            : response.Headers.Location?.OriginalString;
}
