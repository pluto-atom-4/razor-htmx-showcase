using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FactoryApp.Web.Tests;

public static class AuthTestClient
{
    public const string AdminEmail = "admin@example.com";
    public const string SeededPassword = "SecurePassword123!";

    private static readonly Regex AntiforgeryInput = new(
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

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
        var path = LoginPath(returnUrl);
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

    public static async Task<string> FetchAntiforgeryTokenAsync(HttpClient client, string path = "/Login")
    {
        var html = await client.GetStringAsync(path);
        var match = AntiforgeryInput.Match(html);
        Assert.True(match.Success, "Antiforgery token input not found on the page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string LoginPath(string? returnUrl) =>
        returnUrl is null ? "/Login" : $"/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
}
