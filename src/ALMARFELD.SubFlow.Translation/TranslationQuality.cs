using ALMARFELD.SubFlow.Core.Languages;

namespace ALMARFELD.SubFlow.Translation;

public enum SegmentVerdict { Ok, Empty, WrongScript, TooLong, Untranslated, MissingLines }

/// <summary>Heuristic checks that catch typical machine-translation failures without false certainty.</summary>
public static class TranslationQuality
{
    private enum Script { Latin, Cyrillic, Greek, Arabic, Hebrew, Devanagari, Thai, Hangul, Japanese, Han, Bengali, Other }

    private static Script Expected(Language l) => l.Code switch
    {
        "ru" or "uk" or "bg" or "sr" => Script.Cyrillic,
        "el" => Script.Greek,
        "ar" or "fa" or "ur" => Script.Arabic,
        "he" => Script.Hebrew,
        "hi" => Script.Devanagari,
        "bn" => Script.Bengali,
        "th" => Script.Thai,
        "ko" => Script.Hangul,
        "ja" => Script.Japanese,
        "zh" => Script.Han,
        _ => Script.Latin,
    };

    public static SegmentVerdict Check(string source, string? translation, Language sourceLanguage, Language target)
    {
        if (string.IsNullOrWhiteSpace(translation)) return SegmentVerdict.Empty;

        var sourceLetters = source.Count(char.IsLetter);
        var letters = translation.Where(char.IsLetter).ToList();

        // Hallucination guard: output far longer than any plausible translation.
        // CJK ↔ alphabetic changes lengths a lot, so the limit is generous.
        var limit = Math.Max(source.Length * 4, source.Length + 80);
        if (translation.Length > limit) return SegmentVerdict.TooLong;

        // Script guard: a Russian translation should be written mostly in Cyrillic, etc.
        // Short segments (names, "OK") are exempt.
        var expected = Expected(target);
        if (letters.Count >= 6 && expected != Script.Latin)
        {
            var inScript = letters.Count(c => ScriptOf(c) == expected || expected == Script.Japanese && ScriptOf(c) == Script.Han);
            if (inScript < letters.Count * 0.4) return SegmentVerdict.WrongScript;
        }
        if (letters.Count >= 6 && expected == Script.Latin && Expected(sourceLanguage) != Script.Latin)
        {
            var latin = letters.Count(c => ScriptOf(c) == Script.Latin);
            if (latin < letters.Count * 0.5) return SegmentVerdict.WrongScript;
        }

        // Copied unchanged: fine for names, interjections and song lyrics in a third language
        // ("Gohan!", "Huh?", "CHA-LA-HEAD-CHA-LA"), but not for an ordinary sentence of the
        // source language ("So, we finally meet.").
        if (string.Equals(Normalise(source), Normalise(translation), StringComparison.OrdinalIgnoreCase))
        {
            var sentence = LanguageDetector.ContainsCommonWords(source, sourceLanguage.Code) ?? sourceLetters >= 25;
            return sentence ? SegmentVerdict.Untranslated : SegmentVerdict.Ok;
        }

        // Dropped dialogue guard: a two-line cue that comes back as fewer lines and noticeably
        // shorter has probably lost a line ("Born ready!" + "Let's go." → only the first part).
        var sourceLines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Any(char.IsLetter));
        var translatedLines = translation.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        // Word counts, not characters: "That's when I remembered / about you, Kakarrot." →
        // "Așa mi-am amintit de tine, Kakarrot." is complete on one line; a lost line roughly
        // halves the number of words.
        static int Words(string s) => s.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
        if (sourceLines >= 2 && translatedLines < sourceLines &&
            Expected(sourceLanguage) == Script.Latin && Expected(target) == Script.Latin &&
            Words(translation) < Words(source) * 0.6)
            return SegmentVerdict.MissingLines;

        return SegmentVerdict.Ok;
    }

    private static string Normalise(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static Script ScriptOf(char c) => c switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' => Script.Latin,
        >= 'À' and <= 'ɏ' or >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        >= 'Ѐ' and <= 'ӿ' => Script.Cyrillic,
        >= '֐' and <= '׿' => Script.Hebrew,
        >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => Script.Arabic,
        >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
        >= 'ঀ' and <= '৿' => Script.Bengali,
        >= '฀' and <= '๿' => Script.Thai,
        >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' => Script.Hangul,
        >= '぀' and <= 'ヿ' => Script.Japanese,
        >= '一' and <= '鿿' or >= '㐀' and <= '䶿' => Script.Han,
        _ => Script.Other,
    };

    /// <summary>
    /// If the source cue had two lines and the translation came back as one long line, split it
    /// near the middle at a word boundary so the subtitle keeps its shape on screen.
    /// </summary>
    public static string BalanceLines(string source, string translation)
    {
        if (translation.Contains('\n') || !source.Contains('\n') || translation.Length < 40) return translation;
        if (!translation.Contains(' ')) return translation; // CJK/Thai: no safe break points
        var mid = translation.Length / 2;
        int best = -1;
        for (var d = 0; d < mid; d++)
        {
            if (mid + d < translation.Length && translation[mid + d] == ' ') { best = mid + d; break; }
            if (mid - d > 0 && translation[mid - d] == ' ') { best = mid - d; break; }
        }
        return best <= 0 ? translation : translation[..best].TrimEnd() + "\n" + translation[(best + 1)..].TrimStart();
    }
}
