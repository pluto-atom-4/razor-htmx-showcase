using System.Net;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class IndexPageTests(WebAppFactory factory) : IClassFixture<WebAppFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Index_returns_html_listing_seeded_builds()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Q2 2026 Production Run", body);
        Assert.Contains("Legacy System Migration", body);
        Assert.Contains("Completed Build A", body);
    }

    [Fact]
    public async Task Layout_references_htmx_scripts_and_app_css()
    {
        var body = await _client.GetStringAsync("/");

        Assert.Contains("src=\"/lib/htmx/htmx.min.js\"", body);
        Assert.Contains("src=\"/lib/htmx/sse.js\"", body);
        Assert.Contains("href=\"/css/app.css\"", body);
    }
}
