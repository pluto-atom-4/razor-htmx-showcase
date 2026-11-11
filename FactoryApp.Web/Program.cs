using FactoryApp.Domain;
using FactoryApp.Domain.TestFixtures;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FactoryDbContext>((sp, options) =>
    options.UseSqlServer(sp.GetRequiredService<IConfiguration>()["FACTORY_CONNECTION"]
        ?? throw new InvalidOperationException("Set FACTORY_CONNECTION (see .env.example).")));
builder.Services.AddRazorPages();

builder.Services.AddScoped<FactoryApp.Web.Services.AuthService>();
builder.Services.AddScoped<BuildService>();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "factory.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.IsEssential = true;
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = static context =>
        {
            var headers = context.Request.Headers;
            if (headers.ContainsKey("HX-Request"))
            {
                // A 302 would be followed inside the htmx XHR and the login page swapped in; HX-Redirect navigates instead.
                var login = new Uri(context.RedirectUri, UriKind.RelativeOrAbsolute);
                var target = login.IsAbsoluteUri ? login.PathAndQuery : login.OriginalString;
                // Return to the page the user was on, not to the POST URL; only a same-host current URL is trusted.
                if (Uri.TryCreate(headers["HX-Current-URL"].ToString(), UriKind.Absolute, out var current)
                    && string.Equals(current.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
                {
                    target = $"{context.Options.LoginPath}?{context.Options.ReturnUrlParameter}={Uri.EscapeDataString(current.PathAndQuery)}";
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["HX-Redirect"] = target.StartsWith("//", StringComparison.Ordinal)
                    ? context.Options.LoginPath.Value
                    : target;
                return Task.CompletedTask;
            }

            if (string.Equals(headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            {
                // Keep the framework default for plain XHR clients: 401 with the login URL in Location.
                context.Response.Headers.Location = context.RedirectUri;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

var app = builder.Build();
// Opt-in: WebApplicationFactory also runs in Development, so tests must not seed by default.
if (app.Environment.IsDevelopment() && app.Configuration["FACTORY_DEV_SEED"] == "true")
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
    await db.Database.MigrateAsync();
    await FixtureSeeder.SeedAllAsync(db, app.Environment.IsDevelopment());
}
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health", async (FactoryDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503))
    .AllowAnonymous();
app.MapRazorPages();

app.Run();

// Public so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
