using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Subtitles;

namespace ALMARFELD.SubFlow.Core.Translation;

public enum ProviderCost
{
    /// <summary>Runs on this computer. No data leaves the machine, no charges.</summary>
    FreeLocal,
    /// <summary>Cloud API billed by the provider. Subtitle text is sent over the internet.</summary>
    PaidCloud,
}

public sealed record TranslationSegment(int Id, string Text);

/// <summary>Earlier dialogue given to the engine for context only (never translated again).</summary>
public sealed record ContextLine(string Source, string? Translation);

public sealed record TranslationBatchRequest
{
    public required IReadOnlyList<TranslationSegment> Segments { get; init; }
    public required Language Source { get; init; }
    public required Language Target { get; init; }
    public IReadOnlyList<ContextLine> PrecedingContext { get; init; } = Array.Empty<ContextLine>();
    public IReadOnlyList<GlossaryEntry> Glossary { get; init; } = Array.Empty<GlossaryEntry>();
    /// <summary>Additional instructions added on retries (e.g. which ids were missing last time).</summary>
    public string? CorrectionNote { get; init; }
}

/// <summary>Thrown by providers. <see cref="IsTransient"/> errors are retried; others fail immediately.</summary>
public class TranslationProviderException(string message, bool isTransient, Exception? inner = null)
    : Exception(message, inner)
{
    public bool IsTransient { get; } = isTransient;
}

/// <summary>A translation engine. Implementations translate a batch of numbered segments.</summary>
public interface ITranslationProvider
{
    string Id { get; }
    string DisplayName { get; }
    ProviderCost Cost { get; }

    /// <summary>True when subtitle text leaves this computer (cloud APIs, remote servers). The UI must disclose this.</summary>
    bool SendsTextOffMachine { get; }

    /// <summary>Model or API variant; part of the translation cache key.</summary>
    string ModelId { get; }

    /// <summary>Number of segments the engine handles well in one request.</summary>
    int PreferredBatchSize { get; }

    /// <summary>
    /// Provider-side knowledge of language support. For LLMs this is a best-effort statement:
    /// quality varies by model and language.
    /// </summary>
    bool SupportsLanguage(Language language, bool asSource);

    /// <summary>Returns translations keyed by segment id. May return fewer ids than requested; callers validate.</summary>
    Task<IReadOnlyDictionary<int, string>> TranslateBatchAsync(TranslationBatchRequest request, CancellationToken ct);
}

public sealed record DocumentTranslationRequest
{
    public required SubtitleDocument Document { get; init; }
    public required Language Source { get; init; }
    public required Language Target { get; init; }
    public IReadOnlyList<GlossaryEntry> Glossary { get; init; } = Array.Empty<GlossaryEntry>();
    /// <summary>Awaited between batches; used to implement pause.</summary>
    public Func<CancellationToken, Task>? Checkpoint { get; init; }
}

public sealed record TranslationProgress(int TranslatedSegments, int TotalSegments, int CachedSegments);

public sealed record DocumentTranslationResult(SubtitleDocument Document, IReadOnlyList<string> Warnings, int CachedSegments, int TotalSegments);

/// <summary>Translates a complete subtitle document with validation, retries and caching.</summary>
public interface ISubtitleTranslator
{
    ITranslationProvider Provider { get; }
    Task<DocumentTranslationResult> TranslateAsync(DocumentTranslationRequest request, IProgress<TranslationProgress>? progress, CancellationToken ct);
}

public interface ITranslationCache
{
    /// <summary>Looks up many keys at once; missing keys are absent from the result.</summary>
    IReadOnlyDictionary<string, string> GetMany(IEnumerable<string> keys);
    void SetMany(IEnumerable<KeyValuePair<string, string>> entries);
    long Count();
    void Clear();
}

/// <summary>Raised when a document could not be translated completely. No partial track is produced.</summary>
public sealed class IncompleteTranslationException(string message, IReadOnlyList<int> missingIds)
    : Exception(message)
{
    public IReadOnlyList<int> MissingIds { get; } = missingIds;
}
