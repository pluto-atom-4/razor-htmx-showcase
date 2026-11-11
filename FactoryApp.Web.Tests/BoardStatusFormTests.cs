using System.Net;
using System.Text.RegularExpressions;
using FactoryApp.Domain.Entities;
using FactoryApp.Web.Pages;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class BoardStatusFormTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly Regex CardArticle = new("<article id=\"card-([0-9a-fA-F-]{36})\"", RegexOptions.Compiled);

    [Fact]
    public async Task Pending_card_offers_only_the_running_button()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        Assert.Equal(new[] { "Running" }, ButtonValues(form));
    }

    [Fact]
    public async Task Running_card_offers_complete_and_failed_buttons()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Running);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        Assert.Equal(new[] { "Complete", "Failed" }, ButtonValues(form));
    }

    [Fact]
    public async Task Complete_card_renders_no_form()
    {
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Complete);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var body = await client.GetStringAsync("/Board");

        Assert.Contains($"id=\"card-{buildId}\"", body);
        Assert.DoesNotContain($"id=\"form-{buildId}\"", body);
    }

    [Fact]
    public async Task Every_card_has_an_error_slot()
    {
        await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
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
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
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
        var buildId = await BoardDb.AddBuildAsync(factory, BuildStatus.Pending);
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var form = FormFor(await client.GetStringAsync("/Board"), buildId);

        var action = WebUtility.HtmlDecode(Regex.Match(form, "hx-post=\"([^\"]+)\"").Groups[1].Value);
        var token = WebUtility.HtmlDecode(HtmlAssert.InputValue(form, "__RequestVerificationToken"));
        var scrapedBuildId = HtmlAssert.InputValue(form, "BuildId");
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
        Assert.Contains($"id=\"card-{buildId}\"", HtmlAssert.Section(body, "Running"));
        Assert.Equal(BuildStatus.Running, await BoardDb.StatusOfAsync(factory, buildId));
    }

    [Fact]
    public async Task App_script_sets_the_response_handling_table()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var body = await client.GetStringAsync("/app.js");

        Assert.Contains("responseHandling", body);
        var swap422 = body.IndexOf("{code:\"422\",swap:true", StringComparison.Ordinal);
        var generic4xx = body.IndexOf("{code:\"[45]..\"", StringComparison.Ordinal);
        Assert.True(swap422 >= 0 && swap422 < generic4xx, "The 422 entry must come before the generic 4xx entry.");
    }

    [Fact]
    public async Task App_script_leaves_the_page_when_a_response_lands_on_the_login_page()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var body = await client.GetStringAsync("/app.js");

        Assert.Contains("htmx:beforeSwap", body);
        Assert.Contains("'/Login'", body);
        Assert.Contains("htmx:responseError", body);
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
}
