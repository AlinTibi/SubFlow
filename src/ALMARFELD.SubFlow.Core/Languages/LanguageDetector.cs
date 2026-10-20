using System.Globalization;
using System.Text.RegularExpressions;

namespace ALMARFELD.SubFlow.Core.Languages;

public sealed record LanguageGuess(string Code, double Confidence);

/// <summary>
/// Lightweight offline language detector for subtitle text. It combines Unicode script
/// analysis with stop-word frequency. It is deliberately conservative: callers should only
/// trust a result when <see cref="IsReliable"/> returns true, otherwise ask the user.
/// </summary>
public static partial class LanguageDetector
{
    public const double ReliableThreshold = 0.6;

    private static readonly Dictionary<string, string[]> StopWords = new()
    {
        ["en"] = ["the", "and", "you", "that", "is", "to", "of", "it", "what", "this", "are", "have", "with", "not", "we", "for", "was", "i'm", "don't", "your"],
        ["ro"] = ["și", "nu", "este", "că", "în", "pe", "să", "de", "la", "cu", "mai", "ce", "am", "sunt", "eu", "tu", "asta", "un", "o", "ești"],
        ["fr"] = ["le", "la", "les", "et", "est", "pas", "je", "vous", "que", "de", "un", "une", "tu", "il", "ce", "c'est", "qui", "nous", "ne", "dans"],
        ["de"] = ["der", "die", "das", "und", "ist", "nicht", "ich", "du", "sie", "es", "ein", "zu", "wir", "was", "mit", "den", "auf", "ja", "mich", "haben"],
        ["es"] = ["el", "la", "que", "de", "y", "no", "es", "en", "un", "los", "por", "qué", "lo", "se", "me", "con", "una", "está", "para", "yo"],
        ["it"] = ["il", "di", "che", "non", "è", "la", "e", "un", "per", "sono", "mi", "ho", "lo", "ma", "cosa", "questo", "ti", "una", "con", "io"],
        ["pt"] = ["o", "a", "que", "não", "de", "é", "e", "um", "você", "do", "da", "eu", "isso", "uma", "com", "para", "está", "em", "os", "se"],
        ["nl"] = ["de", "het", "een", "en", "is", "niet", "ik", "je", "dat", "van", "wat", "we", "zijn", "op", "met", "hij", "maar", "die", "voor", "jij"],
        ["pl"] = ["nie", "to", "się", "jest", "że", "na", "co", "jak", "w", "i", "mnie", "tak", "ja", "ty", "ale", "z", "do", "czy", "jestem", "tego"],
        ["hu"] = ["a", "az", "és", "nem", "hogy", "egy", "van", "ez", "is", "meg", "mi", "de", "csak", "már", "én", "te", "ő", "volt", "kell", "itt"],
        ["cs"] = ["je", "to", "se", "na", "že", "ne", "a", "v", "jsem", "co", "tak", "jak", "ale", "mi", "by", "jsi", "tady", "taky", "už", "není"],
        ["sk"] = ["je", "to", "sa", "na", "že", "nie", "a", "v", "som", "čo", "tak", "ako", "ale", "mi", "by", "si", "tu", "už", "ste", "aj"],
        ["sv"] = ["och", "är", "det", "att", "jag", "inte", "du", "en", "på", "som", "har", "vi", "med", "för", "vad", "den", "kan", "här", "han", "mig"],
        ["no"] = ["og", "er", "det", "å", "jeg", "ikke", "du", "en", "på", "som", "har", "vi", "med", "for", "hva", "den", "kan", "her", "han", "meg"],
        ["da"] = ["og", "er", "det", "at", "jeg", "ikke", "du", "en", "på", "som", "har", "vi", "med", "for", "hvad", "den", "kan", "her", "han", "mig"],
        ["fi"] = ["on", "ja", "ei", "se", "että", "en", "mitä", "minä", "sinä", "hän", "me", "olen", "tämä", "mutta", "kuin", "oli", "niin", "nyt", "vain", "jos"],
        ["tr"] = ["bir", "ve", "bu", "ne", "ben", "sen", "mi", "değil", "için", "da", "de", "çok", "var", "o", "ama", "evet", "hayır", "gibi", "şey", "neden"],
        ["id"] = ["yang", "dan", "ini", "itu", "tidak", "aku", "kau", "kamu", "apa", "di", "ke", "dari", "ada", "saya", "akan", "dengan", "untuk", "bisa", "sudah", "kita"],
        ["hr"] = ["je", "i", "da", "se", "ne", "u", "to", "na", "sam", "što", "ti", "mi", "li", "su", "ali", "za", "ja", "kako", "ovo", "nije"],
        ["sl"] = ["je", "in", "da", "se", "ne", "v", "to", "na", "sem", "kaj", "ti", "mi", "pa", "so", "ali", "za", "jaz", "kako", "tega", "ni"],
        ["lt"] = ["ir", "yra", "ne", "aš", "tu", "kad", "tai", "jis", "su", "į", "kas", "bet", "man", "mes", "jūs", "čia", "taip", "kaip", "buvo", "dabar"],
        ["lv"] = ["un", "ir", "ne", "es", "tu", "ka", "tas", "viņš", "ar", "uz", "kas", "bet", "man", "mēs", "jūs", "šeit", "jā", "kā", "bija", "tagad"],
        ["et"] = ["ja", "on", "ei", "see", "et", "ma", "sa", "mis", "kas", "ta", "me", "olen", "aga", "kui", "oli", "nii", "nüüd", "siin", "mind", "sind"],
        ["vi"] = ["không", "tôi", "là", "có", "của", "và", "được", "anh", "em", "này", "một", "người", "đã", "cho", "với", "những", "gì", "đi", "bạn", "ở"],
        ["ru"] = ["и", "не", "в", "что", "я", "ты", "это", "на", "он", "с", "как", "мы", "вы", "так", "но", "да", "нет", "мне", "меня", "есть"],
        ["uk"] = ["і", "не", "в", "що", "я", "ти", "це", "на", "він", "з", "як", "ми", "ви", "так", "але", "та", "ні", "мені", "мене", "є"],
        ["bg"] = ["и", "не", "в", "че", "аз", "ти", "това", "на", "той", "с", "как", "ние", "вие", "да", "но", "е", "съм", "ще", "ми", "какво"],
        ["sr"] = ["и", "не", "у", "да", "ја", "ти", "то", "на", "он", "се", "како", "ми", "ви", "је", "али", "сам", "шта", "ће", "ме", "ово"],
    };

    // Characters that strongly indicate a specific language within a script.
    private static readonly Dictionary<string, string> DistinctiveLetters = new()
    {
        ["ro"] = "ăâîșțşţ",
        ["de"] = "äöüß",
        ["pl"] = "ąćęłńśźż",
        ["cs"] = "ěřůč",
        ["sk"] = "ľĺŕô",
        ["hu"] = "őű",
        ["tr"] = "ğışİ",
        ["es"] = "ñ¿¡",
        ["pt"] = "ãõç",
        ["fr"] = "œèêëç",
        ["sv"] = "åäö",
        ["da"] = "æøå",
        ["no"] = "æøå",
        ["lv"] = "āēīūļņģķ",
        ["lt"] = "ąčęėįšųūž",
        ["et"] = "õäöü",
        ["vi"] = "ơưđạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ",
        ["uk"] = "іїєґ",
        ["sr"] = "ђјљњћџ",
        ["bg"] = "ъщ",
    };

    public static bool IsReliable(LanguageGuess? guess) => guess is not null && guess.Confidence >= ReliableThreshold;

    public static LanguageGuess? Detect(IEnumerable<string> lines)
    {
        var text = string.Join(' ', lines.Take(400));
        if (text.Length < 20) return null;

        // 1. Script analysis.
        int latin = 0, cyrillic = 0, greek = 0, arabic = 0, hebrew = 0, devanagari = 0, thai = 0,
            hangul = 0, kana = 0, han = 0, letters = 0, persianMarkers = 0, urduMarkers = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            switch (ch)
            {
                case >= 'A' and <= 'ɏ': latin++; break;
                case >= 'Ḁ' and <= 'ỿ': latin++; break;
                case >= 'Ͱ' and <= 'Ͽ': greek++; break;
                case >= 'Ѐ' and <= 'ӿ': cyrillic++; break;
                case >= '֐' and <= '׿': hebrew++; break;
                case >= '؀' and <= 'ۿ':
                    arabic++;
                    if (ch is 'پ' or 'چ' or 'ژ' or 'گ' or 'ی' or 'ک') persianMarkers++;
                    if (ch is 'ٹ' or 'ڈ' or 'ڑ' or 'ں' or 'ے') urduMarkers++;
                    break;
                case >= 'ऀ' and <= 'ॿ': devanagari++; break;
                case >= '฀' and <= '๿': thai++; break;
                case >= '가' and <= '힯': hangul++; break;
                case >= '぀' and <= 'ヿ': kana++; break;
                case >= '一' and <= '鿿': han++; break;
            }
        }
        if (letters == 0) return null;

        double Share(int n) => (double)n / letters;
        if (Share(hangul) > 0.3) return new("ko", 0.95);
        if (Share(kana) > 0.1) return new("ja", 0.9);
        if (Share(han) > 0.3) return new("zh", 0.85);
        if (Share(thai) > 0.3) return new("th", 0.95);
        if (Share(devanagari) > 0.3) return new("hi", 0.85);
        if (Share(greek) > 0.3) return new("el", 0.95);
        if (Share(hebrew) > 0.3) return new("he", 0.95);
        if (Share(arabic) > 0.3)
        {
            if (urduMarkers > 3) return new("ur", 0.7);
            if (persianMarkers > 5) return new("fa", 0.7);
            return new("ar", 0.8);
        }

        // 2. Stop-word frequency for alphabetic scripts.
        var words = WordRegex().Matches(text.ToLower(CultureInfo.InvariantCulture))
            .Select(m => m.Value).ToList();
        if (words.Count < 8) return null;

        var isCyrillic = Share(cyrillic) > 0.5;
        var candidates = StopWords
            .Where(kv => IsCyrillicLanguage(kv.Key) == isCyrillic)
            .ToList();

        var scores = new Dictionary<string, double>();
        foreach (var (code, stops) in candidates)
        {
            var set = new HashSet<string>(stops);
            double hits = words.Count(set.Contains);
            double score = hits / words.Count;
            if (DistinctiveLetters.TryGetValue(code, out var distinct))
            {
                var distinctHits = text.Count(c => distinct.Contains(char.ToLowerInvariant(c)));
                score += Math.Min(0.15, distinctHits / (double)letters * 3);
            }
            scores[code] = score;
        }

        var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
        if (ranked.Count == 0 || ranked[0].Value < 0.08) return null;
        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Value : 0;
        // Confidence grows with absolute score and with the margin over the runner-up.
        var margin = (best.Value - second) / best.Value;
        var confidence = Math.Clamp(0.35 + margin * 0.6 + Math.Min(best.Value, 0.4), 0, 0.99);
        return new LanguageGuess(best.Key, confidence);
    }

    private static bool IsCyrillicLanguage(string code) => code is "ru" or "uk" or "bg" or "sr";

    [GeneratedRegex(@"[\p{L}']+")]
    private static partial Regex WordRegex();
}
