using System.Text.Json;

namespace ALMARFELD.SubFlow.Core.Languages;

/// <summary>
/// Registry of supported subtitle languages. Built-in languages can be extended by
/// a user-supplied <c>languages.json</c> file without code changes (see <see cref="LoadExtensions"/>).
/// </summary>
public sealed class LanguageCatalog
{
    private readonly List<Language> _languages;

    public static LanguageCatalog Default { get; } = new(BuiltIn());

    public LanguageCatalog(IEnumerable<Language> languages)
    {
        _languages = languages
            .GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<Language> All => _languages;

    public Language? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        // Exact ISO 639-1 match first, then any other form.
        return _languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
               ?? _languages.FirstOrDefault(l => l.Matches(code));
    }

    public Language Get(string code) =>
        Find(code) ?? throw new KeyNotFoundException($"Unknown language code '{code}'.");

    public IEnumerable<Language> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _languages;
        query = query.Trim();
        return _languages.Where(l =>
            l.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            l.NativeName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            l.Matches(query));
    }

    /// <summary>
    /// Returns a new catalog with languages from a JSON file merged in (entries with the same
    /// ISO 639-1 code replace built-in ones). The file is an array of <see cref="Language"/> objects.
    /// </summary>
    public LanguageCatalog LoadExtensions(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return this;
        var extra = JsonSerializer.Deserialize<List<Language>>(File.ReadAllText(jsonPath), ExtensionJson) ?? new();
        var valid = extra.Where(l => !string.IsNullOrWhiteSpace(l.Iso6391) && !string.IsNullOrWhiteSpace(l.Iso6392B));
        return new LanguageCatalog(_languages.Concat(valid));
    }

    private static readonly JsonSerializerOptions ExtensionJson = new() { PropertyNameCaseInsensitive = true };

    private static Language[] BuiltIn() => new[]
    {
        // Name, Native, 639-1, 639-2/B, 639-2/T, RTL, DeepL, Google
        new Language("English", "English", "en", "eng", "eng", false, "EN-US", "en"),
        new Language("Romanian", "Română", "ro", "rum", "ron", false, "RO", "ro"),
        new Language("French", "Français", "fr", "fre", "fra", false, "FR", "fr"),
        new Language("German", "Deutsch", "de", "ger", "deu", false, "DE", "de"),
        new Language("Spanish", "Español", "es", "spa", "spa", false, "ES", "es"),
        new Language("Italian", "Italiano", "it", "ita", "ita", false, "IT", "it"),
        new Language("Portuguese", "Português", "pt", "por", "por", false, "PT-PT", "pt"),
        new Language("Dutch", "Nederlands", "nl", "dut", "nld", false, "NL", "nl"),
        new Language("Polish", "Polski", "pl", "pol", "pol", false, "PL", "pl"),
        new Language("Hungarian", "Magyar", "hu", "hun", "hun", false, "HU", "hu"),
        new Language("Czech", "Čeština", "cs", "cze", "ces", false, "CS", "cs"),
        new Language("Slovak", "Slovenčina", "sk", "slo", "slk", false, "SK", "sk"),
        new Language("Bulgarian", "Български", "bg", "bul", "bul", false, "BG", "bg"),
        new Language("Greek", "Ελληνικά", "el", "gre", "ell", false, "EL", "el"),
        new Language("Turkish", "Türkçe", "tr", "tur", "tur", false, "TR", "tr"),
        new Language("Ukrainian", "Українська", "uk", "ukr", "ukr", false, "UK", "uk"),
        new Language("Russian", "Русский", "ru", "rus", "rus", false, "RU", "ru"),
        new Language("Arabic", "العربية", "ar", "ara", "ara", true, "AR", "ar"),
        new Language("Hebrew", "עברית", "he", "heb", "heb", true, "HE", "iw"),
        new Language("Hindi", "हिन्दी", "hi", "hin", "hin", false, null, "hi"),
        new Language("Chinese", "中文", "zh", "chi", "zho", false, "ZH", "zh-CN"),
        new Language("Japanese", "日本語", "ja", "jpn", "jpn", false, "JA", "ja"),
        new Language("Korean", "한국어", "ko", "kor", "kor", false, "KO", "ko"),
        new Language("Indonesian", "Bahasa Indonesia", "id", "ind", "ind", false, "ID", "id"),
        new Language("Vietnamese", "Tiếng Việt", "vi", "vie", "vie", false, "VI", "vi"),
        new Language("Thai", "ไทย", "th", "tha", "tha", false, "TH", "th"),
        new Language("Swedish", "Svenska", "sv", "swe", "swe", false, "SV", "sv"),
        new Language("Norwegian", "Norsk", "no", "nor", "nor", false, "NB", "no"),
        new Language("Danish", "Dansk", "da", "dan", "dan", false, "DA", "da"),
        new Language("Finnish", "Suomi", "fi", "fin", "fin", false, "FI", "fi"),
        new Language("Croatian", "Hrvatski", "hr", "hrv", "hrv", false, null, "hr"),
        new Language("Serbian", "Српски", "sr", "srp", "srp", false, null, "sr"),
        new Language("Slovenian", "Slovenščina", "sl", "slv", "slv", false, "SL", "sl"),
        new Language("Lithuanian", "Lietuvių", "lt", "lit", "lit", false, "LT", "lt"),
        new Language("Latvian", "Latviešu", "lv", "lav", "lav", false, "LV", "lv"),
        new Language("Estonian", "Eesti", "et", "est", "est", false, "ET", "et"),
        new Language("Persian", "فارسی", "fa", "per", "fas", true, null, "fa"),
        new Language("Malay", "Bahasa Melayu", "ms", "may", "msa", false, null, "ms"),
        new Language("Filipino", "Filipino", "tl", "fil", "fil", false, null, "tl"),
        new Language("Catalan", "Català", "ca", "cat", "cat", false, null, "ca"),
        new Language("Bengali", "বাংলা", "bn", "ben", "ben", false, null, "bn"),
        new Language("Urdu", "اردو", "ur", "urd", "urd", true, null, "ur"),
    };
}
