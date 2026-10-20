using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ALMARFELD.SubFlow.Core.Glossary;

/// <summary>
/// A terminology rule. When <see cref="Target"/> is empty or <see cref="Protected"/> is set the term
/// must appear unchanged in the translation (character names, techniques, brands).
/// </summary>
public sealed class GlossaryEntry
{
    public string Source { get; set; } = "";
    public string? Target { get; set; }
    /// <summary>ISO 639-1 code of the target language this rule applies to; null = all languages.</summary>
    public string? TargetLanguage { get; set; }
    public bool CaseSensitive { get; set; }
    public bool Protected { get; set; }
    public string? Notes { get; set; }

    [JsonIgnore]
    public string ExpectedTarget => Protected || string.IsNullOrWhiteSpace(Target) ? Source : Target!;

    public bool AppliesTo(string targetLanguage) =>
        string.IsNullOrWhiteSpace(TargetLanguage) ||
        TargetLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase);

    public bool OccursIn(string text) => BuildRegex(Source).IsMatch(text);

    public bool ExpectedTargetOccursIn(string text) => BuildRegex(ExpectedTarget).IsMatch(text);

    internal Regex BuildRegex(string term)
    {
        var options = RegexOptions.CultureInvariant | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        // Whole-word match where the term starts/ends with a letter or digit; otherwise a plain substring.
        var escaped = Regex.Escape(term.Trim());
        var start = term.Length > 0 && char.IsLetterOrDigit(term.Trim()[0]) ? @"(?<![\p{L}\p{N}])" : "";
        var end = term.Length > 0 && char.IsLetterOrDigit(term.Trim()[^1]) ? @"(?![\p{L}\p{N}])" : "";
        return new Regex(start + escaped + end, options);
    }
}

public sealed class GlossaryDocument
{
    public string Name { get; set; } = "Glossary";
    public List<GlossaryEntry> Entries { get; set; } = new();
}

public static class GlossaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Merges glossaries; later ones (e.g. per-project) override earlier ones (global) for the same term.</summary>
    public static IReadOnlyList<GlossaryEntry> Merge(params IEnumerable<GlossaryEntry>?[] glossaries)
    {
        var result = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in glossaries)
        {
            if (g is null) continue;
            foreach (var e in g.Where(e => !string.IsNullOrWhiteSpace(e.Source)))
                result[$"{e.Source.Trim()}|{e.TargetLanguage}"] = e;
        }
        return result.Values.ToList();
    }

    /// <summary>Entries relevant for a piece of source text and a target language.</summary>
    public static IReadOnlyList<GlossaryEntry> Relevant(IEnumerable<GlossaryEntry> entries, IEnumerable<string> sourceTexts, string targetLanguage)
    {
        var texts = sourceTexts.ToList();
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Source) && e.AppliesTo(targetLanguage))
            // Language-specific rules take priority over generic ones for the same term.
            .GroupBy(e => e.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.TargetLanguage is not null).First())
            .Where(e => texts.Any(e.OccursIn))
            .ToList();
    }

    /// <summary>Returns glossary terms whose expected rendering is missing from a translation.</summary>
    public static IReadOnlyList<GlossaryEntry> FindViolations(string source, string translation, IEnumerable<GlossaryEntry> relevant) =>
        relevant.Where(e => e.OccursIn(source) && !e.ExpectedTargetOccursIn(translation)).ToList();

    /// <summary>
    /// Editor helper: replaces occurrences of each source term that were left untranslated with the
    /// configured target term. Protected terms are already identical and remain unchanged.
    /// </summary>
    public static string Apply(string text, IEnumerable<GlossaryEntry> entries, string targetLanguage)
    {
        foreach (var e in entries.Where(e => e.AppliesTo(targetLanguage) && !e.Protected && !string.IsNullOrWhiteSpace(e.Target)))
            text = e.BuildRegex(e.Source).Replace(text, e.Target!.Replace("$", "$$"));
        return text;
    }

    public static GlossaryDocument Load(string path)
    {
        if (!File.Exists(path)) return new GlossaryDocument();
        return Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? new GlossaryDocument { Name = Path.GetFileNameWithoutExtension(path), Entries = ReadCsv(File.ReadAllText(path)) }
            : JsonSerializer.Deserialize<GlossaryDocument>(File.ReadAllText(path), JsonOptions) ?? new GlossaryDocument();
    }

    public static void Save(GlossaryDocument glossary, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var content = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? WriteCsv(glossary.Entries)
            : JsonSerializer.Serialize(glossary, JsonOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(true));
        File.Move(temp, path, overwrite: true);
    }

    // CSV columns: source,target,target_language,case_sensitive,protected,notes
    public static string WriteCsv(IEnumerable<GlossaryEntry> entries)
    {
        var sb = new StringBuilder("source,target,target_language,case_sensitive,protected,notes\r\n");
        foreach (var e in entries)
        {
            sb.Append(Csv(e.Source)).Append(',').Append(Csv(e.Target)).Append(',').Append(Csv(e.TargetLanguage)).Append(',')
              .Append(e.CaseSensitive ? "true" : "false").Append(',').Append(e.Protected ? "true" : "false").Append(',')
              .Append(Csv(e.Notes)).Append("\r\n");
        }
        return sb.ToString();
    }

    public static List<GlossaryEntry> ReadCsv(string content)
    {
        var rows = ParseCsv(content.TrimStart('﻿'));
        var list = new List<GlossaryEntry>();
        foreach (var row in rows.Skip(rows.Count > 0 && rows[0].FirstOrDefault()?.Trim().Equals("source", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0))
        {
            if (row.Count == 0 || string.IsNullOrWhiteSpace(row[0])) continue;
            string? Col(int i) => i < row.Count && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;
            list.Add(new GlossaryEntry
            {
                Source = row[0].Trim(),
                Target = Col(1),
                TargetLanguage = Col(2),
                CaseSensitive = bool.TryParse(Col(3), out var cs) && cs,
                Protected = bool.TryParse(Col(4), out var p) && p,
                Notes = Col(5),
            });
        }
        return list;
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    public static string Describe(GlossaryEntry e) => e.Protected || string.IsNullOrWhiteSpace(e.Target)
        ? string.Create(CultureInfo.InvariantCulture, $"\"{e.Source}\" → keep unchanged")
        : string.Create(CultureInfo.InvariantCulture, $"\"{e.Source}\" → \"{e.Target}\"");
}
