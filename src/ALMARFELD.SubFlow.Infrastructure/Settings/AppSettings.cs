using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Infrastructure.Tools;

namespace ALMARFELD.SubFlow.Infrastructure.Settings;

public sealed class OllamaSettings
{
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public string? Model { get; set; }
    public double Temperature { get; set; } = 0.2;
    public int ContextLength { get; set; } = 8192;
    public int BatchSize { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class OpenAiCompatibleSettings
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    /// <summary>DPAPI-protected API key (current Windows user only).</summary>
    public string? ProtectedApiKey { get; set; }
    public int BatchSize { get; set; } = 30;
}

public sealed class DeepLSettings
{
    public string? ProtectedApiKey { get; set; }
    /// <summary>True for "Free" API keys (ending in ":fx"), which use api-free.deepl.com.</summary>
    public bool UseFreeEndpoint { get; set; } = true;
}

public sealed class GoogleSettings
{
    public string? ProtectedApiKey { get; set; }
}

public sealed class AppSettings
{
    public string ProviderId { get; set; } = "ollama";
    public OllamaSettings Ollama { get; set; } = new();
    public OpenAiCompatibleSettings OpenAi { get; set; } = new();
    public DeepLSettings DeepL { get; set; } = new();
    public GoogleSettings Google { get; set; } = new();
    public ToolPathOverrides Tools { get; set; } = new();

    public string SourceLanguage { get; set; } = "auto";
    public List<string> TargetLanguages { get; set; } = new();
    public ExistingTrackPolicy ExistingTrackPolicy { get; set; } = ExistingTrackPolicy.Skip;
    public ExistingOutputPolicy ExistingOutputPolicy { get; set; } = ExistingOutputPolicy.Skip;
    public OutputMode OutputMode { get; set; } = OutputMode.Mkv;
    public string? OutputFolder { get; set; }
    public bool PreserveFolderStructure { get; set; } = true;
    public bool MakeFirstNewTrackDefault { get; set; }
    public int Concurrency { get; set; } = 1;
    public bool RecursiveScan { get; set; } = true;

    /// <summary>UI language (culture name). Not related to subtitle languages.</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>Ask GitHub for a newer release at startup. Off by default: no network calls unasked.</summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>Last main window bounds in physical pixels (left, top, right, bottom) and whether it was maximised.</summary>
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>Cloud providers require one explicit confirmation per session before sending text.</summary>
    [JsonIgnore]
    public bool CloudConsentGivenThisSession { get; set; }
}

public sealed class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public SettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file must not prevent start-up; keep a copy for inspection.
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json), new UTF8Encoding(false));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>
/// Protects API keys with Windows DPAPI (CurrentUser scope): the stored value can only be
/// decrypted by the same Windows account on the same machine. Keys are never logged.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ALMARFELD.SubFlow.v1");

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secure key storage requires Windows.");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
