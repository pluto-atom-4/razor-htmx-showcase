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
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
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

        var pending = Section(body, "Pending");
        Assert.Contains("Q2 2026 Production Run", pending);
        Assert.DoesNotContain("Legacy System Migration", pending);

        var running = Section(body, "Running");
        Assert.Contains("Legacy System Migration", running);
        Assert.Contains("Testing Phase Build", running);
        Assert.DoesNotContain("Completed Build A", running);

        var complete = Section(body, "Complete");
        Assert.Contains("Completed Build A", complete);
        Assert.Contains("History Build 1", complete);

        Assert.Contains("Failed Build B", Section(body, "Failed"));
    }

    [Fact]
    public async Task Board_exposes_stable_column_and_card_ids()
    {
        var body = await _client.GetStringAsync("/Board");

        Assert.Contains("id=\"cards-Running\"", body);
        Assert.Contains("id=\"card-10000000-0000-0000-0000-000000000002\"", body);
    }

    [Fact]
    public async Task Anonymous_board_redirects_to_login()
    {
        using var anonymous = AuthTestClient.CreateClient(factory);

        var response = await anonymous.GetAsync("/Board");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", PathAndQuery(response));
    }

    private static string Section(string body, string status)
    {
        var start = body.IndexOf($"id=\"column-{status}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Column {status} not found.");
        var next = body.IndexOf("id=\"column-", start + 1, StringComparison.Ordinal);
        return next < 0 ? body[start..] : body[start..next];
    }

    private static string? PathAndQuery(HttpResponseMessage response) =>
        response.Headers.Location is { IsAbsoluteUri: true } absolute
            ? absolute.PathAndQuery
            : response.Headers.Location?.OriginalString;
}
