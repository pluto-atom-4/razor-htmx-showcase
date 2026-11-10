using System.Net;
using System.Text.Json;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class HealthEndpointTests : IClassFixture<WebAppFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_returns_ok_when_database_reachable()
    {
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }
}
