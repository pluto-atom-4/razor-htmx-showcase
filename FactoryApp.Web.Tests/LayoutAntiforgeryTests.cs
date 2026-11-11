using System.Net;
using System.Text.RegularExpressions;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class LayoutAntiforgeryTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private static readonly Regex LogoutForm = new("<form[^>]*action=\"/Login\\?handler=Logout\"[^>]*>[\\s\\S]*?</form>", RegexOptions.Compiled);

    [Fact]
    public async Task Authenticated_layout_has_csrf_meta_app_script_and_logout_form()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(HtmlAssert.MetaContent(body, "csrf-token"));
        Assert.Contains("src=\"/app.js\"", body);
        var form = LogoutForm.Match(body);
        Assert.True(form.Success, "Logout form posting to Login?handler=Logout not found.");
        Assert.Contains("name=\"__RequestVerificationToken\"", form.Value);
    }

    [Fact]
    public async Task Anonymous_login_page_has_csrf_meta_but_no_logout_form()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var body = await client.GetStringAsync(AuthTestClient.LoginPath);

        Assert.NotEmpty(HtmlAssert.MetaContent(body, "csrf-token"));
        Assert.DoesNotContain("handler=Logout", body);
    }

    [Fact]
    public async Task App_script_text_is_served_anonymously_with_the_expected_header_name()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.GetAsync("/app.js");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("htmx:configRequest", body);
        Assert.Contains("headers['RequestVerificationToken']", body);
        Assert.Contains("location.origin", body);
    }

    [Fact]
    public async Task Logout_form_token_ends_session_and_redirects_to_login()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client, "/");
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };

        var logout = await client.PostAsync(AuthTestClient.LogoutPath, new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(AuthTestClient.LoginPath, logout.Headers.Location?.ToString());
        Assert.True(AuthTestClient.ClearsAuthCookie(logout));

        var index = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, index.StatusCode);
        Assert.StartsWith(AuthTestClient.LoginPath, HtmlAssert.PathAndQuery(index));
    }

    [Fact]
    public async Task Logout_accepts_token_from_request_header()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        var token = await MetaCsrfTokenAsync(client);
        using var request = AuthTestClient.PostWithHeaderToken(
            AuthTestClient.LogoutPath, token, new FormUrlEncodedContent(new Dictionary<string, string>()));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Logout_rejects_wrong_request_header_token()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);
        _ = await MetaCsrfTokenAsync(client);
        using var request = AuthTestClient.PostWithHeaderToken(
            AuthTestClient.LogoutPath, "not-the-token", new FormUrlEncodedContent(new Dictionary<string, string>()));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var index = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
    }

    private static async Task<string> MetaCsrfTokenAsync(HttpClient client)
    {
        var body = await client.GetStringAsync("/");
        return WebUtility.HtmlDecode(HtmlAssert.MetaContent(body, "csrf-token"));
    }
}
