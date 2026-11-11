using System.Text.RegularExpressions;

namespace FactoryApp.Web.Tests;

internal static class HtmlAssert
{
    private static readonly Regex InputTag = new("<input\\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new("<meta\\b[^>]*>", RegexOptions.Compiled);

    public static string Section(string body, string status)
    {
        var start = body.IndexOf($"id=\"column-{status}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Column {status} not found.");
        var end = body.IndexOf("</section>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Column {status} is not closed.");
        return body[start..end];
    }

    public static string InputValue(string html, string inputName) =>
        AttributeValue(InputTag, html, "name", inputName, "value", $"Input {inputName} with a value not found.");

    public static string MetaContent(string html, string metaName) =>
        AttributeValue(MetaTag, html, "name", metaName, "content", $"Meta {metaName} with a content not found.");

    // The cookie middleware redirects with an absolute URL; page redirects are relative.
    public static string? PathAndQuery(HttpResponseMessage response) =>
        response.Headers.Location is { IsAbsoluteUri: true } absolute
            ? absolute.PathAndQuery
            : response.Headers.Location?.OriginalString;

    private static string AttributeValue(
        Regex tagPattern, string html, string nameAttribute, string name, string valueAttribute, string message)
    {
        var match = tagPattern.Matches(html)
            .Select(tag => tag.Value)
            .Where(tag => Regex.IsMatch(tag, $"\\s{nameAttribute}=\"{Regex.Escape(name)}\""))
            .Select(tag => Regex.Match(tag, $"\\s{valueAttribute}=\"([^\"]+)\""))
            .FirstOrDefault(m => m.Success);
        Assert.True(match is not null, message);
        return match!.Groups[1].Value;
    }
}
