using System.Net;
using System.Text.RegularExpressions;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class LayoutAntiforgeryTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly Regex CsrfMeta = new("<meta name=\"csrf-token\" content=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex LogoutForm = new("<form[^>]*action=\"/Login\\?handler=Logout\"[^>]*>[\\s\\S]*?</form>", RegexOptions.Compiled);

    [Fact]
    public async Task Authenticated_layout_has_csrf_meta_app_script_and_logout_form()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var meta = CsrfMeta.Match(body);
        Assert.True(meta.Success);
        Assert.False(string.IsNullOrEmpty(meta.Groups[1].Value));
        Assert.Contains("src=\"/app.js\"", body);
        var form = LogoutForm.Match(body);
        Assert.True(form.Success, "Logout form posting to Login?handler=Logout not found.");
        Assert.Contains("name=\"__RequestVerificationToken\"", form.Value);
    }

    [Fact]
    public async Task Anonymous_login_page_has_csrf_meta_but_no_logout_form()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var body = await client.GetStringAsync("/Login");

        Assert.Matches(CsrfMeta, body);
        Assert.DoesNotContain("handler=Logout", body);
    }

    [Fact]
    public async Task App_script_is_served_anonymously_with_header_hook()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.GetAsync("/app.js");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("htmx:configRequest", body);
        Assert.Contains("RequestVerificationToken", body);
    }

    [Fact]
    public async Task Logout_form_token_ends_session_and_redirects_to_login()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/");
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };

        var logout = await client.PostAsync("/Login?handler=Logout", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/Login", logout.Headers.Location?.ToString());
        Assert.True(ClearsAuthCookie(logout));

        var index = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, index.StatusCode);
        Assert.StartsWith("/Login", PathAndQuery(index));
    }

    [Fact]
    public async Task Logout_accepts_token_from_request_header()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await MetaCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Login?handler=Logout")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        };
        request.Headers.Add("RequestVerificationToken", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Logout_rejects_wrong_request_header_token()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        _ = await MetaCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Login?handler=Logout")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        };
        request.Headers.Add("RequestVerificationToken", "not-the-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> MetaCsrfTokenAsync(HttpClient client)
    {
        var body = await client.GetStringAsync("/");
        var meta = CsrfMeta.Match(body);
        Assert.True(meta.Success, "csrf-token meta tag not found.");
        return WebUtility.HtmlDecode(meta.Groups[1].Value);
    }

    private static string? PathAndQuery(HttpResponseMessage response) =>
        response.Headers.Location is { IsAbsoluteUri: true } absolute
            ? absolute.PathAndQuery
            : response.Headers.Location?.OriginalString;

    private static bool ClearsAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => Regex.IsMatch(c, @"^factory\.auth=;")
            && Regex.IsMatch(c, @"expires=Thu, 01 Jan 1970|max-age=0", RegexOptions.IgnoreCase));
}
