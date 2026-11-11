using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryApp.Web.Tests;

public static class AuthTestClient
{
    public const string AdminEmail = "admin@example.com";
    public const string SeededPassword = "SecurePassword123!";
    public const string LoginPath = "/Login";
    public const string LogoutPath = "/Login?handler=Logout";
    public const string ChangeStatusPath = "/Board?handler=ChangeStatus";
    public const string HtmlMediaType = "text/html";

    // Secure cookies are only sent over https, and the auth cookie is Secure outside Development.
    public static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });

    public static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string email, string password, string? returnUrl = null)
    {
        var path = LoginUrl(returnUrl);
        var token = await FetchAntiforgeryTokenAsync(client, path);

        var form = new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        };
        if (returnUrl is not null)
        {
            form["ReturnUrl"] = returnUrl;
        }

        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = CreateClient(factory);
        var response = await LoginAsync(client, AdminEmail, SeededPassword);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static async Task<string> FetchAntiforgeryTokenAsync(HttpClient client, string path = LoginPath)
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(HtmlAssert.InputValue(html, "__RequestVerificationToken"));
    }

    public static HttpRequestMessage PostWithHeaderToken(string path, string token, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("RequestVerificationToken", token);
        return request;
    }

    public static bool SetsAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => Regex.IsMatch(c, @"^factory\.auth=[^;]+"));

    public static bool ClearsAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(c => Regex.IsMatch(c, @"^factory\.auth=;")
            && Regex.IsMatch(c, @"expires=Thu, 01 Jan 1970|max-age=0", RegexOptions.IgnoreCase));

    private static string LoginUrl(string? returnUrl) =>
        returnUrl is null ? LoginPath : $"{LoginPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
}
