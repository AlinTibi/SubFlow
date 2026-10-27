using ALMARFELD.SubFlow.Core;
using System.Security.Cryptography;
using System.Text;
using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Core.Translation;
using ALMARFELD.SubFlow.Translation.Llm;

namespace ALMARFELD.SubFlow.Translation;

public sealed class DocumentTranslatorOptions
{
    /// <summary>Attempts per batch before it is split into smaller batches.</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>Earlier cues passed as context with each batch.</summary>
    public int ContextLines { get; init; } = 4;
    /// <summary>Upper bound of source characters per request (keeps prompts within context windows).</summary>
    public int MaxBatchCharacters { get; init; } = 2400;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Translates a whole subtitle document through an <see cref="ITranslationProvider"/>:
/// context-aware batches, strict per-segment validation, retries with targeted correction notes,
/// batch splitting as fallback, and a persistent cache. A segment that cannot be translated after
/// all attempts makes the document fail — nothing is invented and nothing is silently dropped.
/// </summary>
public sealed class DocumentTranslator : ISubtitleTranslator
{
    private readonly ITranslationCache? _cache;
    private readonly DocumentTranslatorOptions _options;

    public DocumentTranslator(ITranslationProvider provider, ITranslationCache? cache, DocumentTranslatorOptions? options = null)
    {
        Provider = provider;
        _cache = cache;
        _options = options ?? new DocumentTranslatorOptions();
    }

    public ITranslationProvider Provider { get; }

    private sealed record Unit(SubtitleCue Cue, TranslatableText Split, string Key);

    public async Task<DocumentTranslationResult> TranslateAsync(DocumentTranslationRequest request, IProgress<TranslationProgress>? progress, CancellationToken ct)
    {
        var doc = request.Document;
        var warnings = new List<string>();
        var glossaryForTarget = request.Glossary.Where(e => e.AppliesTo(request.Target.Code)).ToList();
        var glossaryHash = Hash(string.Join("\u001f", glossaryForTarget.Select(e => $"{e.Source}\u001e{e.ExpectedTarget}\u001e{e.CaseSensitive}")));

        var units = doc.Cues
            .Where(c => !c.IsNonDialogue)
            .Select(c => (Cue: c, Split: SubtitleMarkup.Split(c.Text, doc.Format)))
            .Where(x => !x.Split.IsEmpty)
            .Select(x => new Unit(x.Cue, x.Split, CacheKey(request.Source, request.Target, glossaryHash, x.Split.Plain)))
            .ToList();

        var translations = new Dictionary<int, string>(); // cue index → translated plain text
        var cached = _cache?.GetMany(units.Select(u => u.Key)) ?? new Dictionary<string, string>();
        foreach (var u in units)
            if (cached.TryGetValue(u.Key, out var t)) translations[u.Cue.Index] = t;
        var cachedCount = translations.Count;
        progress?.Report(new TranslationProgress(translations.Count, units.Count, cachedCount));

        var pending = units.Where(u => !translations.ContainsKey(u.Cue.Index)).ToList();
        foreach (var batch in MakeBatches(pending))
        {
            ct.ThrowIfCancellationRequested();
            if (request.Checkpoint is not null) await request.Checkpoint(ct);

            var context = BuildContext(units, batch[0], translations);
            var result = await TranslateWithRecoveryAsync(batch, context, request, glossaryForTarget, warnings, depth: 0, ct);
            foreach (var (index, text) in result) translations[index] = text;

            _cache?.SetMany(batch.Where(u => result.ContainsKey(u.Cue.Index))
                .Select(u => new KeyValuePair<string, string>(u.Key, result[u.Cue.Index])));
            progress?.Report(new TranslationProgress(translations.Count, units.Count, cachedCount));
        }

        // Every dialogue unit must have a translation; otherwise the document is rejected.
        var missing = units.Where(u => !translations.ContainsKey(u.Cue.Index)).Select(u => u.Cue.Index).ToList();
        if (missing.Count > 0)
            throw new IncompleteTranslationException($"{Text.Count(missing.Count, "subtitle segment")} could not be translated.", missing);

        // Glossary audit over the final text (cached segments included).
        var violations = 0;
        foreach (var u in units)
            if (GlossaryService.FindViolations(u.Split.Plain, translations[u.Cue.Index], glossaryForTarget).Count > 0) violations++;
        if (violations > 0)
            warnings.Add($"{Text.Count(violations, "segment")} without the expected glossary terms; review them in the Subtitle Editor.");

        var texts = units.ToDictionary(u => u.Cue.Index,
            u => u.Split.Rebuild(TranslationQuality.BalanceLines(u.Split.Plain, translations[u.Cue.Index])));
        return new DocumentTranslationResult(doc.WithTexts(texts), warnings.Distinct().ToList(), cachedCount, units.Count);
    }

    private IEnumerable<List<Unit>> MakeBatches(List<Unit> pending)
    {
        var batch = new List<Unit>();
        var chars = 0;
        foreach (var u in pending)
        {
            if (batch.Count > 0 && (batch.Count >= Provider.PreferredBatchSize || chars + u.Split.Plain.Length > _options.MaxBatchCharacters))
            {
                yield return batch;
                batch = new List<Unit>();
                chars = 0;
            }
            batch.Add(u);
            chars += u.Split.Plain.Length;
        }
        if (batch.Count > 0) yield return batch;
    }

    private List<ContextLine> BuildContext(List<Unit> units, Unit first, Dictionary<int, string> translations)
    {
        var position = units.IndexOf(first);
        return units.Skip(Math.Max(0, position - _options.ContextLines)).Take(Math.Min(position, _options.ContextLines))
            .Select(u => new ContextLine(u.Split.Plain, translations.TryGetValue(u.Cue.Index, out var t) ? t : null))
            .ToList();
    }

    /// <summary>Translates a batch; retries missing/invalid segments, then splits the batch as a fallback.</summary>
    private async Task<Dictionary<int, string>> TranslateWithRecoveryAsync(List<Unit> batch, List<ContextLine> context,
        DocumentTranslationRequest request, IReadOnlyList<GlossaryEntry> glossary, List<string> warnings, int depth, CancellationToken ct)
    {
        var accepted = new Dictionary<int, string>();
        var softAccepted = new Dictionary<int, string>(); // suspicious but plausible (e.g. unchanged text)
        var remaining = batch;
        string? correction = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts && remaining.Count > 0; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // Local ids 1..n keep prompts short and unambiguous.
            var segments = remaining.Select((u, i) => new TranslationSegment(i + 1, u.Split.Plain)).ToList();
            var relevantGlossary = GlossaryService.Relevant(glossary, segments.Select(s => s.Text), request.Target.Code);

            IReadOnlyDictionary<int, string> reply;
            try
            {
                reply = await Provider.TranslateBatchAsync(new TranslationBatchRequest
                {
                    Segments = segments,
                    Source = request.Source,
                    Target = request.Target,
                    PrecedingContext = context,
                    Glossary = relevantGlossary,
                    CorrectionNote = correction,
                }, ct);
            }
            catch (TranslationProviderException ex) when (ex.IsTransient)
            {
                // Timeouts, unreadable or runaway answers: retry, and after the last attempt fall
                // through to splitting the batch, which smaller requests usually survive.
                if (attempt == _options.MaxAttempts) break;
                await Task.Delay(TimeSpan.FromTicks(_options.RetryDelay.Ticks * attempt), ct);
                continue;
            }

            var stillMissing = new List<Unit>();
            var problems = new List<string>();
            for (var i = 0; i < remaining.Count; i++)
            {
                var unit = remaining[i];
                reply.TryGetValue(i + 1, out var text);
                text = text?.Replace("\r\n", "\n").Trim();
                var verdict = TranslationQuality.Check(unit.Split.Plain, text, request.Source, request.Target);
                var glossaryMisses = verdict == SegmentVerdict.Ok
                    ? GlossaryService.FindViolations(unit.Split.Plain, text!, relevantGlossary)
                    : Array.Empty<GlossaryEntry>();

                if (verdict == SegmentVerdict.Ok && glossaryMisses.Count == 0)
                {
                    accepted[unit.Cue.Index] = text!;
                }
                else if (verdict is SegmentVerdict.Untranslated or SegmentVerdict.MissingLines || (verdict == SegmentVerdict.Ok && glossaryMisses.Count > 0))
                {
                    // Plausible output; try once more, keep it if the retry is not better.
                    softAccepted[unit.Cue.Index] = text!;
                    stillMissing.Add(unit);
                    problems.Add(verdict switch
                    {
                        SegmentVerdict.Untranslated => $"id {i + 1} was returned untranslated",
                        SegmentVerdict.MissingLines => $"id {i + 1} lost part of its text; translate every line of it",
                        _ => $"id {i + 1} must use the glossary term(s) {string.Join(", ", glossaryMisses.Select(g => $"\"{g.ExpectedTarget}\""))}",
                    });
                }
                else
                {
                    stillMissing.Add(unit);
                    problems.Add(verdict switch
                    {
                        SegmentVerdict.Empty => $"id {i + 1} was missing or empty",
                        SegmentVerdict.WrongScript => $"id {i + 1} was not written in {request.Target.Name}",
                        SegmentVerdict.TooLong => $"id {i + 1} contained text that is not in the source",
                        _ => $"id {i + 1} was invalid",
                    });
                }
            }

            remaining = stillMissing;
            correction = remaining.Count == 0 ? null
                : $"Return all {remaining.Count} segment(s) again. Problems: {string.Join("; ", problems.Take(10))}.";
        }

        // Segments that only failed soft checks keep their best answer and are reported.
        var softKept = 0;
        foreach (var unit in remaining.ToList())
        {
            if (softAccepted.TryGetValue(unit.Cue.Index, out var soft))
            {
                accepted[unit.Cue.Index] = soft;
                remaining.Remove(unit);
                softKept++;
            }
        }
        if (softKept > 0)
            warnings.Add($"{Text.Count(softKept, "segment")} may be incomplete or untranslated after retries; review them in the Subtitle Editor.");
        if (remaining.Count == 0) return accepted;

        // Fallback: smaller batches are easier for weak models.
        if (remaining.Count > 1 && depth < 4)
        {
            var half = remaining.Count / 2;
            foreach (var part in new[] { remaining.Take(half).ToList(), remaining.Skip(half).ToList() })
            {
                var partResult = await TranslateWithRecoveryAsync(part, context, request, glossary, warnings, depth + 1, ct);
                foreach (var kv in partResult) accepted[kv.Key] = kv.Value;
            }
            return accepted;
        }

        var ids = string.Join(", ", remaining.Select(u => u.Cue.Index));
        throw new IncompleteTranslationException(
            $"The engine did not return a valid translation for subtitle #{ids} after {_options.MaxAttempts} attempts.",
            remaining.Select(u => u.Cue.Index).ToList());
    }

    private string CacheKey(Language source, Language target, string glossaryHash, string plain) =>
        Hash($"{Provider.Id}\u001f{Provider.ModelId}\u001f{LlmPrompt.Version}\u001f{source.Code}\u001f{target.Code}\u001f{glossaryHash}\u001f{plain}");

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
