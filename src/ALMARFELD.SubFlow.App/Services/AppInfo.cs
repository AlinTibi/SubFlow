using System.Reflection;

namespace ALMARFELD.SubFlow.App.Services;

/// <summary>Product identity shown in the UI. The repository URL comes from Directory.Build.props.</summary>
public static class AppInfo
{
    public const string ProductName = "ALMARFELD SubFlow";
    public const string Website = "https://almarfeld.com";

    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    public static Version Version { get; } = Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => Version.ToString(3);

    public static string Copyright =>
        Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "Copyright © ALMARFELD";

    /// <summary>For example https://github.com/owner/repo; empty until the project is published.</summary>
    public static string RepositoryUrl =>
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value?.TrimEnd('/') ?? "";

    public static bool HasRepository => RepositoryUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    public static string ReleasesUrl => HasRepository ? RepositoryUrl + "/releases" : Website;

    public static string IssuesUrl => HasRepository ? RepositoryUrl + "/issues" : Website;
}
