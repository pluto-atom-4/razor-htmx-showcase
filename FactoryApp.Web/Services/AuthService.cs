using System.Security.Claims;
using System.Text.Json;
using BCrypt.Net;
using FactoryApp.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Web.Services;

public sealed class AuthService(FactoryDbContext db)
{
    // Unknown emails verify against this so both failure paths pay the same BCrypt cost.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("unused-dummy-password");

    private const int MaxPasswordBytes = 72;

    public string HashPassword(string password)
        => BCrypt.Net.BCrypt.HashPassword(password);

    public bool VerifyPassword(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (SaltParseException)
        {
            return false;
        }
    }

    public async Task<ClaimsPrincipal?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        // BCrypt ignores bytes past 72, so a longer password would match on its first 72 bytes only.
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password)
            || System.Text.Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes)
        {
            return null;
        }

        // Do not lower-case: the SQL Server collation already matches emails case-insensitively.
        var normalizedEmail = email.Trim();
        var user = await db.AuthUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        var passwordMatches = VerifyPassword(password, user?.PasswordHash ?? DummyHash);
        if (user is null || !passwordMatches)
        {
            return null;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        // Malformed claims JSON is skipped so one bad row cannot block login.
        try
        {
            var customClaims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(user.Claims) ?? new();
            foreach (var (claimName, element) in customClaims)
            {
                // A custom claim must never add a second Role or identity claim.
                if (element.ValueKind != JsonValueKind.String || claims.Any(c => c.Type == claimName))
                {
                    continue;
                }

                claims.Add(new Claim(claimName, element.GetString()!));
            }
        }
        catch (JsonException)
        {
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
