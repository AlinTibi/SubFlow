namespace ALMARFELD.SubFlow.Infrastructure.Persistence;

/// <summary>
/// Local storage locations. Settings roam (%APPDATA%); the database, work files and logs stay on
/// this machine (%LOCALAPPDATA%). Nothing is stored anywhere else.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? roamingRoot = null, string? localRoot = null)
    {
        RoamingRoot = roamingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ALMARFELD", "SubFlow");
        LocalRoot = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALMARFELD", "SubFlow");
        Directory.CreateDirectory(RoamingRoot);
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(WorkDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public string RoamingRoot { get; }
    public string LocalRoot { get; }
    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");
    public string GlobalGlossaryFile => Path.Combine(RoamingRoot, "glossary.json");
    public string LanguagesExtensionFile => Path.Combine(RoamingRoot, "languages.json");
    public string DatabaseFile => Path.Combine(LocalRoot, "subflow.db");
    public string WorkDirectory => Path.Combine(LocalRoot, "work");
    public string LogDirectory => Path.Combine(LocalRoot, "logs");

    /// <summary>Per-project glossary file name looked up in an import folder.</summary>
    public const string ProjectGlossaryFileName = "subflow.glossary.json";
}
