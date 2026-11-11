using System.Net;
using FactoryApp.Domain.Entities;
using FactoryApp.Web.Pages;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class HtmxAuthRedirectTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly string Station = BoardModel.Stations[0];

    [Fact]
    public async Task Anonymous_htmx_post_gets_401_with_relative_hx_redirect_and_writes_nothing()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = AuthTestClient.CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthTestClient.ChangeStatusPath)
        {
            Content = BoardDb.MoveForm(null, buildId, "Running", Station),
        };
        request.Headers.Add("HX-Request", "true");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=", HxRedirect(response));
        Assert.Null(response.Headers.Location);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(BuildStatus.Pending, await BoardDb.StatusOfAsync(factory, buildId));
        Assert.Empty(await BoardDb.HistoryAsync(factory, buildId));
    }

    [Fact]
    public async Task Anonymous_htmx_get_of_board_gets_401_with_hx_redirect_to_return_url()
    {
        using var client = AuthTestClient.CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Board");
        request.Headers.Add("HX-Request", "true");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=%2FBoard", HxRedirect(response));
    }

    [Fact]
    public async Task Anonymous_get_of_board_without_htmx_header_still_redirects_with_302()
    {
        using var client = AuthTestClient.CreateClient(factory);

        using var response = await client.GetAsync("/Board");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=", HtmlAssert.PathAndQuery(response));
        Assert.False(response.Headers.Contains("HX-Redirect"));
    }

    [Fact]
    public async Task Authenticated_htmx_get_of_board_is_200()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Board");
        request.Headers.Add("HX-Request", "true");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("HX-Redirect"));
    }

    [Fact]
    public async Task Hx_redirect_returns_to_the_current_page_not_the_post_url()
    {
        using var client = AuthTestClient.CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthTestClient.ChangeStatusPath)
        {
            Content = BoardDb.MoveForm(null, Guid.NewGuid(), "Running", Station),
        };
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Current-URL", "https://localhost/Board");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal($"{AuthTestClient.LoginPath}?ReturnUrl=%2FBoard", HxRedirect(response));
    }

    [Fact]
    public async Task Hx_current_url_on_another_host_is_ignored()
    {
        using var client = AuthTestClient.CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Board");
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Current-URL", "https://evil.example/steal");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var target = HxRedirect(response);
        Assert.DoesNotContain("evil", target);
        Assert.StartsWith($"{AuthTestClient.LoginPath}?ReturnUrl=%2FBoard", target);
    }

    [Fact]
    public async Task Plain_xhr_keeps_the_framework_default_of_401_with_a_location_header()
    {
        using var client = AuthTestClient.CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Board");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith(AuthTestClient.LoginPath, HtmlAssert.PathAndQuery(response));
        Assert.False(response.Headers.Contains("HX-Redirect"));
    }

    private static string HxRedirect(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("HX-Redirect", out var values), "HX-Redirect header missing.");
        return Assert.Single(values);
    }
}
