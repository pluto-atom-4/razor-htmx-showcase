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
            if (context.Request.Headers.ContainsKey("HX-Request"))
            {
                // A 302 would be followed inside the htmx XHR and the login page swapped in; HX-Redirect navigates instead.
                var uri = new Uri(context.RedirectUri, UriKind.RelativeOrAbsolute);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["HX-Redirect"] = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
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
