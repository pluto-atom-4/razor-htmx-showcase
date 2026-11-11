using System.Net;
using FactoryApp.Domain.Entities;
using FactoryApp.Web.Pages;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardChangeStatusTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly string Station = BoardModel.Stations[0];

    [Fact]
    public async Task Valid_move_returns_out_of_band_columns_and_writes_one_history_row()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var pending = HtmlAssert.Section(body, "Pending");
        var running = HtmlAssert.Section(body, "Running");
        Assert.Contains("hx-swap-oob=\"outerHTML\"", pending);
        Assert.Contains("hx-swap-oob=\"outerHTML\"", running);
        Assert.DoesNotContain($"id=\"card-{buildId}\"", pending);
        Assert.Contains($"id=\"card-{buildId}\"", running);

        Assert.Equal(BuildStatus.Running, await BoardDb.StatusOfAsync(factory, buildId));
        var row = Assert.Single(await BoardDb.HistoryAsync(factory, buildId));
        Assert.Equal(Station, row.Station);
        Assert.Equal(AuthTestClient.AdminEmail, row.Operator);
        Assert.Equal("Started", row.EventType);
    }

    [Fact]
    public async Task Rejected_move_returns_422_fragment_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Complete", Station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("class=\"error\"", body);
        Assert.Contains("Cannot move build from Pending to Complete.", body);
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Theory]
    [InlineData("NOPE-9")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Station_outside_the_list_returns_422_and_writes_nothing(string station)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Running", station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Unknown station.", await response.Content.ReadAsStringAsync());
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("3,1")]
    [InlineData("not-a-status")]
    [InlineData("")]
    public async Task Undefined_or_malformed_status_returns_422_and_writes_nothing(string status)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, status, Station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("class=\"error\"", await response.Content.ReadAsStringAsync());
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Fact]
    public async Task Unknown_build_returns_404()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, Guid.NewGuid(), "Running", Station));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_antiforgery_token_returns_400_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(null, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Fact]
    public async Task Antiforgery_token_in_header_is_accepted()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var request = AuthTestClient.PostWithHeaderToken(
            AuthTestClient.ChangeStatusPath, token, BoardDb.MoveForm(null, buildId, "Running", Station));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BuildStatus.Running, await BoardDb.StatusOfAsync(factory, buildId));
    }

    [Fact]
    public async Task Anonymous_post_redirects_to_login_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var anonymous = AuthTestClient.CreateClient(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(anonymous, AuthTestClient.LoginPath);

        using var response = await anonymous.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(AuthTestClient.LoginPath, HtmlAssert.PathAndQuery(response));
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Fact]
    public async Task Second_move_from_running_writes_a_completed_row()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var first = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Running", Station));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await client.PostAsync(AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Complete", Station));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(BuildStatus.Complete, await BoardDb.StatusOfAsync(factory, buildId));
        var history = await BoardDb.HistoryAsync(factory, buildId);
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
}
