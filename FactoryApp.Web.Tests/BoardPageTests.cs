using System.Net;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardPageTests(WebAppFactory factory) : IClassFixture<WebAppFactory>, IAsyncLifetime
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
    public async Task Board_returns_html_with_columns_in_status_order()
    {
        var response = await _client.GetAsync("/Board");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AuthTestClient.HtmlMediaType, response.Content.Headers.ContentType?.MediaType);
        var pending = body.IndexOf("id=\"column-Pending\"", StringComparison.Ordinal);
        var running = body.IndexOf("id=\"column-Running\"", StringComparison.Ordinal);
        var complete = body.IndexOf("id=\"column-Complete\"", StringComparison.Ordinal);
        var failed = body.IndexOf("id=\"column-Failed\"", StringComparison.Ordinal);
        Assert.True(
            pending >= 0 && pending < running && running < complete && complete < failed,
            "Columns are not rendered in Pending, Running, Complete, Failed order.");
    }

    [Fact]
    public async Task Board_places_seeded_builds_in_their_status_column()
    {
        var body = await _client.GetStringAsync("/Board");

        var pending = HtmlAssert.Section(body, "Pending");
        Assert.Contains("Q2 2026 Production Run", pending);
        Assert.DoesNotContain("Legacy System Migration", pending);

        var running = HtmlAssert.Section(body, "Running");
        Assert.Contains("Legacy System Migration", running);
        Assert.Contains("Testing Phase Build", running);
        Assert.DoesNotContain("Completed Build A", running);

        var complete = HtmlAssert.Section(body, "Complete");
        Assert.Contains("Completed Build A", complete);
        Assert.Contains("History Build 1", complete);

        Assert.Contains("Failed Build B", HtmlAssert.Section(body, "Failed"));
    }

    [Fact]
    public async Task Board_exposes_stable_column_and_card_ids()
    {
        var body = await _client.GetStringAsync("/Board");

        var running = HtmlAssert.Section(body, "Running");
        Assert.Contains("id=\"cards-Running\"", running);
        Assert.Contains("id=\"count-Running\"", running);
        Assert.Contains("id=\"card-10000000-0000-0000-0000-000000000002\"", running);
    }

    [Fact]
    public async Task Anonymous_board_redirects_to_login()
    {
        using var anonymous = AuthTestClient.CreateClient(factory);

        var response = await anonymous.GetAsync("/Board");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(AuthTestClient.LoginPath, HtmlAssert.PathAndQuery(response));
    }
}
