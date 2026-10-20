using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ALMARFELD.SubFlow.App.Services;

public sealed record UpdateInfo(Version Latest, string Tag, string PageUrl)
{
    public bool IsNewer => Latest > AppInfo.Version;
}

/// <summary>
/// Reads the latest release from the GitHub API. Only called when the user enabled update checks or
/// pressed "Check now"; it sends nothing but a plain anonymous request.
/// </summary>
public static class UpdateChecker
{
    public static async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct)
    {
        if (!AppInfo.HasRepository) return null;
        var repo = AppInfo.RepositoryUrl["https://github.com/".Length..];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ALMARFELD-SubFlow", AppInfo.VersionText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.GetAsync(new Uri($"https://api.github.com/repos/{repo}/releases/latest"), ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        var page = json.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() ?? AppInfo.ReleasesUrl : AppInfo.ReleasesUrl;
        return Version.TryParse(tag.TrimStart('v', 'V'), out var version) ? new UpdateInfo(version, tag, page) : null;
    }
}
