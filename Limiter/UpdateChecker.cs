using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Limiter;

internal static class UpdateChecker
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Limiter-update-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static Version? ParseNewerVersion(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = root.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Major < 0 || version.Minor < 0) return null;
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build), Math.Max(0, current.Revision));
        return normalized > installed ? version : null;
    }

    internal static async Task<Version?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync("https://api.github.com/repos/emi-ran/Limiter/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseNewerVersion(json, Assembly.GetExecutingAssembly().GetName().Version!);
    }
}
