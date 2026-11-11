using System.Net;
using FactoryApp.Domain.Entities;
using FactoryApp.Web.Pages;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class EndpointContractTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly string Station = BoardModel.Stations[0];

    [Theory]
    [InlineData("/health")]
    [InlineData("/Login")]
    [InlineData("/app.js")]
    [InlineData("/css/app.css")]
    [InlineData("/lib/htmx/htmx.min.js")]
    [InlineData("/lib/htmx/sse.js")]
    public async Task Anonymous_get_of_public_paths_is_200(string path)
    {
        using var client = AuthTestClient.CreateClient(factory);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Index")]
    [InlineData("/Board")]
    public async Task Anonymous_get_of_protected_pages_redirects_to_login(string path)
    {
        using var client = AuthTestClient.CreateClient(factory);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=", HtmlAssert.PathAndQuery(response));
    }

    [Fact]
    public async Task Anonymous_get_of_an_unknown_path_redirects_to_login_so_paths_are_not_revealed()
    {
        using var client = AuthTestClient.CreateClient(factory);

        using var response = await client.GetAsync("/no-such-page");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=", HtmlAssert.PathAndQuery(response));
    }

    [Fact]
    public async Task Authenticated_get_of_an_unknown_path_is_404()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        using var response = await client.GetAsync("/no-such-page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_change_status_post_without_a_token_is_rejected_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = AuthTestClient.CreateClient(factory);

        using var response = await client.PostAsync(
            AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(null, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(AuthTestClient.LoginPath, HtmlAssert.PathAndQuery(response));
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_antiforgery_token_returns_400_and_writes_nothing(bool viaHeader)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        _ = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = viaHeader
            ? await client.SendAsync(AuthTestClient.PostWithHeaderToken(
                AuthTestClient.ChangeStatusPath, "not-the-token", BoardDb.MoveForm(null, buildId, "Running", Station)))
            : await client.PostAsync(
                AuthTestClient.ChangeStatusPath, BoardDb.MoveForm("not-the-token", buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Fact]
    public async Task Applied_move_response_is_an_html_fragment_without_the_layout()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Running", Station));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AuthTestClient.HtmlMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_move_response_is_an_html_fragment_without_the_layout()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, "Complete", Station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(AuthTestClient.HtmlMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BuildStatus.Complete, "Running")]
    [InlineData(BuildStatus.Complete, "Pending")]
    [InlineData(BuildStatus.Failed, "Running")]
    [InlineData(BuildStatus.Running, "Running")]
    public async Task Terminal_or_same_status_move_returns_422_and_writes_nothing(BuildStatus from, string to)
    {
        var buildId = await BoardDb.AddBuildAsync(factory, from);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/Board");

        using var response = await client.PostAsync(
            AuthTestClient.ChangeStatusPath, BoardDb.MoveForm(token, buildId, to, Station));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains($"Cannot move build from {from} to {to}.", await response.Content.ReadAsStringAsync());
        Assert.Equal(from, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Fact]
    public async Task Login_cookie_is_http_only_same_site_lax_and_secure()
    {
        using var client = AuthTestClient.CreateClient(factory);

        using var response = await AuthTestClient.LoginAsync(client, AuthTestClient.AdminEmail, AuthTestClient.SeededPassword);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var authCookie = Assert.Single(cookies, c => c.StartsWith("factory.auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
    }
}
