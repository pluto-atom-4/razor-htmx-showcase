using System.Net;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class LoginPageTests(WebAppFactory factory) : IClassFixture<WebAppFactory>
{
    private const string InvalidCredentialsMessage = "Invalid email or password";

    [Fact]
    public async Task Anonymous_root_redirects_to_login_with_return_url()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login?ReturnUrl=%2F", HtmlAssert.PathAndQuery(response));
    }

    [Fact]
    public async Task Health_is_reachable_anonymously()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_page_is_reachable_anonymously()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.GetAsync(AuthTestClient.LoginPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Htmx_script_is_reachable_anonymously()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.GetAsync("/lib/htmx/htmx.min.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        using var client = AuthTestClient.CreateClient(factory);
        var form = new Dictionary<string, string>
        {
            ["Email"] = AuthTestClient.AdminEmail,
            ["Password"] = AuthTestClient.SeededPassword,
        };

        var response = await client.PostAsync(AuthTestClient.LoginPath, new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Valid_credentials_set_auth_cookie_and_redirect_to_index()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await AuthTestClient.LoginAsync(client, AuthTestClient.AdminEmail, AuthTestClient.SeededPassword);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
        Assert.True(AuthTestClient.SetsAuthCookie(response));

        var index = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_shows_generic_error_without_auth_cookie()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await AuthTestClient.LoginAsync(client, AuthTestClient.AdminEmail, "wrong-password");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(InvalidCredentialsMessage, body);
        Assert.False(AuthTestClient.SetsAuthCookie(response));
    }

    [Fact]
    public async Task Unknown_email_shows_same_generic_error_without_auth_cookie()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await AuthTestClient.LoginAsync(client, $"nobody-{Guid.NewGuid():N}@example.com", AuthTestClient.SeededPassword);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(InvalidCredentialsMessage, body);
        Assert.False(AuthTestClient.SetsAuthCookie(response));
    }

    [Theory]
    [InlineData("/Index", "/Index")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\t/evil.example", "/")]
    public async Task Return_url_is_honored_only_when_local(string returnUrl, string expectedLocation)
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await AuthTestClient.LoginAsync(client, AuthTestClient.AdminEmail, AuthTestClient.SeededPassword, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Logout_post_without_antiforgery_token_is_rejected()
    {
        using var client = AuthTestClient.CreateClient(factory);

        var response = await client.PostAsync(AuthTestClient.LogoutPath, new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_post_with_valid_token_is_accepted_and_redirects_to_login()
    {
        using var client = AuthTestClient.CreateClient(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client);
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = token };

        var response = await client.PostAsync(AuthTestClient.LogoutPath, new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(AuthTestClient.LoginPath, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Authenticated_get_login_redirects_to_index()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync(AuthTestClient.LoginPath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_logout_path_does_not_end_the_session()
    {
        using var client = await AuthTestClient.CreateAuthenticatedClientAsync(factory);

        await client.GetAsync("/Logout");
        var index = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
    }

    [Fact]
    public async Task Invalid_model_redisplay_does_not_echo_the_password()
    {
        using var client = AuthTestClient.CreateClient(factory);
        var token = await AuthTestClient.FetchAntiforgeryTokenAsync(client);
        var form = new Dictionary<string, string>
        {
            ["Email"] = "not-an-email",
            ["Password"] = AuthTestClient.SeededPassword,
            ["__RequestVerificationToken"] = token,
        };

        var response = await client.PostAsync(AuthTestClient.LoginPath, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(AuthTestClient.SeededPassword, body);
    }
}
