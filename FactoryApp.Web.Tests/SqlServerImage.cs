using System.Text.RegularExpressions;

namespace FactoryApp.Web.Tests;

internal static class SqlServerImage
{
    public static readonly string Reference = Read();

    private static string Read()
    {
        var composeFile = FindComposeFile(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"docker-compose.yml not found in any parent of '{AppContext.BaseDirectory}'.");

        var matches = Regex.Matches(
            File.ReadAllText(composeFile),
            @"^\s*image:\s*(mcr\.microsoft\.com/mssql/server[^\s#""']+)",
            RegexOptions.Multiline);

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one SQL Server image in '{composeFile}', found {matches.Count}.");
        }

        return matches[0].Groups[1].Value;
    }

    private static string? FindComposeFile(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
