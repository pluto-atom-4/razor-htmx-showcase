using System.Security.Claims;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using FactoryApp.Domain.TestFixtures;
using FactoryApp.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Web.Tests;

[Trait("Category", "Integration")]
public class AuthServiceTests(SqlFixture sql) : IClassFixture<SqlFixture>, IAsyncLifetime
{
    private const string SeededPassword = "SecurePassword123!";
    private const string AdminEmail = "admin@example.com";
    private static readonly Guid AdminId = new("00000000-0000-0000-0000-000000000002");

    public static IEnumerable<object?[]> MissingCredentials() =>
    [
        [null, SeededPassword],
        ["", SeededPassword],
        [AdminEmail, null],
        [AdminEmail, ""],
    ];

    public async Task InitializeAsync()
    {
        await using var ctx = CreateContext();
        await FixtureSeeder.SeedAllAsync(ctx, isDevelopment: true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Valid_credentials_return_authenticated_principal_with_seeded_identity()
    {
        await using var ctx = CreateContext();
        var stored = await ctx.AuthUsers.AsNoTracking().SingleAsync(u => u.Email == AdminEmail);

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(AdminEmail, SeededPassword);

        Assert.NotNull(principal);
        Assert.True(principal.Identity?.IsAuthenticated == true);
        Assert.Equal(AdminId.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal(stored.Role.ToString(), principal.FindFirst(ClaimTypes.Role)?.Value);
    }

    [Fact]
    public async Task Wrong_password_returns_null()
    {
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(AdminEmail, "wrong-password");

        Assert.Null(principal);
    }

    [Fact]
    public async Task Unknown_email_returns_null()
    {
        await using var ctx = CreateContext();
        var unknownEmail = $"nobody-{Guid.NewGuid():N}@example.com";

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(unknownEmail, SeededPassword);

        Assert.Null(principal);
    }

    [Fact]
    public async Task Mixed_case_email_with_surrounding_whitespace_authenticates()
    {
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync("  Admin@Example.COM \t", SeededPassword);

        Assert.NotNull(principal);
        Assert.Equal(AdminId.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [Theory]
    [MemberData(nameof(MissingCredentials))]
    public async Task Null_or_empty_credentials_return_null(string email, string password)
    {
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(email, password);

        Assert.Null(principal);
    }

    [Fact]
    public void Hash_round_trips_and_malformed_hash_is_false()
    {
        using var ctx = CreateContext();
        var service = new AuthService(ctx);
        var hash = service.HashPassword("round-trip-password");

        Assert.True(service.VerifyPassword("round-trip-password", hash));
        Assert.False(service.VerifyPassword("other-password", hash));
        Assert.False(service.VerifyPassword("round-trip-password", "not-a-bcrypt-hash"));
    }

    [Fact]
    public async Task Custom_claims_json_is_added_to_principal()
    {
        var email = await InsertUserAsync("{\"station\":\"A1\"}");
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(email, SeededPassword);

        Assert.NotNull(principal);
        Assert.Equal("A1", principal.FindFirst("station")?.Value);
    }

    [Fact]
    public async Task Malformed_claims_json_is_ignored()
    {
        var email = await InsertUserAsync("{not json");
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(email, SeededPassword);

        Assert.NotNull(principal);
        Assert.True(principal.Identity?.IsAuthenticated == true);
        Assert.Null(principal.FindFirst("station"));
    }

    [Fact]
    public async Task Custom_claims_cannot_add_reserved_role_or_identity_claims()
    {
        var email = await InsertUserAsync(
            "{\"http://schemas.microsoft.com/ws/2008/06/identity/claims/role\":\"Admin\","
            + "\"http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier\":\"someone-else\"}");
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(email, SeededPassword);

        Assert.NotNull(principal);
        Assert.False(principal.IsInRole("Admin"));
        Assert.Single(principal.FindAll(System.Security.Claims.ClaimTypes.Role));
        Assert.Single(principal.FindAll(System.Security.Claims.ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task Null_custom_claim_value_is_skipped_and_login_still_works()
    {
        var email = await InsertUserAsync("{\"station\":null,\"shift\":\"night\"}");
        await using var ctx = CreateContext();

        var principal = await new AuthService(ctx).ValidateCredentialsAsync(email, SeededPassword);

        Assert.NotNull(principal);
        Assert.Null(principal.FindFirst("station"));
        Assert.Equal("night", principal.FindFirst("shift")?.Value);
    }

    [Fact]
    public async Task Password_longer_than_72_bytes_is_rejected_even_if_prefix_matches()
    {
        var exactly72 = new string('p', 72);
        var email = $"long-{Guid.NewGuid():N}@example.com";
        await using var ctx = CreateContext();
        var service = new AuthService(ctx);
        ctx.AuthUsers.Add(new AuthUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = service.HashPassword(exactly72),
            Claims = "{}",
        });
        await ctx.SaveChangesAsync();

        Assert.NotNull(await service.ValidateCredentialsAsync(email, exactly72));
        Assert.Null(await service.ValidateCredentialsAsync(email, exactly72 + "x"));
    }

    private FactoryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<string> InsertUserAsync(string claimsJson)
    {
        var email = $"claims-{Guid.NewGuid():N}@example.com";
        await using var ctx = CreateContext();
        var seeded = await ctx.AuthUsers.AsNoTracking().SingleAsync(u => u.Email == AdminEmail);
        ctx.AuthUsers.Add(new AuthUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = seeded.PasswordHash,
            Claims = claimsJson,
        });
        await ctx.SaveChangesAsync();
        return email;
    }
}
