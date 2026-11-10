using FactoryApp.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FactoryDbContext>((sp, options) =>
    options.UseSqlServer(sp.GetRequiredService<IConfiguration>()["FACTORY_CONNECTION"]
        ?? throw new InvalidOperationException("Set FACTORY_CONNECTION (see .env.example).")));
builder.Services.AddRazorPages();

var app = builder.Build();
app.UseStaticFiles();

app.MapGet("/health", async (FactoryDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503));
app.MapRazorPages();

app.Run();

// Public so WebApplicationFactory<Program> can host the app in tests.
public partial class Program;
