using System.Collections.Concurrent;
using System.Diagnostics;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Core.Translation;

namespace ALMARFELD.SubFlow.Core.Processing;

public sealed record FileProgress(JobItem Item, double Fraction, string Stage);

/// <summary>
/// Processes one video: probe → choose source track → extract → translate each target language →
/// remux into a temporary MKV → validate → atomic rename. The source file is only ever read.
/// </summary>
public sealed class FileProcessor
{
    private readonly IMediaProbe _probe;
    private readonly ISubtitleExtractor _extractor;
    private readonly IReadOnlyList<IMuxer> _muxers;
    private readonly ISubtitleTranslator _translator;
    private readonly LanguageCatalog _catalog;
    private readonly string _workRoot;
    private readonly Func<JobItem, IReadOnlyList<GlossaryEntry>>? _glossary;
    private readonly PauseGate? _pause;
    private readonly ISubtitleOcrEngine? _ocr;

    // Output paths claimed by items currently being processed (prevents two items writing one file).
    private static readonly ConcurrentDictionary<string, Guid> ReservedOutputs = new(StringComparer.OrdinalIgnoreCase);

    public FileProcessor(IMediaProbe probe, ISubtitleExtractor extractor, IReadOnlyList<IMuxer> muxers,
        ISubtitleTranslator translator, LanguageCatalog catalog, string workRoot,
        Func<JobItem, IReadOnlyList<GlossaryEntry>>? glossary = null, PauseGate? pause = null, ISubtitleOcrEngine? ocr = null)
    {
        _ocr = ocr;
        _probe = probe;
        _extractor = extractor;
        _muxers = muxers;
        _translator = translator;
        _catalog = catalog;
        _workRoot = workRoot;
        _glossary = glossary;
        _pause = pause;
    }

    public async Task ProcessAsync(JobItem item, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var workDir = Path.Combine(_workRoot, item.Id.ToString("N"));
        string? reserved = null;
        string? tempOutput = null;
        item.Status = JobStatus.Running;
        item.Message = null;
        item.Warnings.Clear();
        item.SkippedLanguages.Clear();
        item.EngineDescription = $"{_translator.Provider.DisplayName} · {_translator.Provider.ModelId}";
        void Report(double f, string stage)
        {
            item.Progress = Math.Clamp(f, 0, 1);
            item.Message = stage;
            item.UpdatedUtc = DateTime.UtcNow;
            progress?.Report(new FileProgress(item, item.Progress, stage));
        }

        try
        {
            if (_pause is not null) await _pause.WaitIfPausedAsync(ct);
            Report(0, "Reading video");

            var sourceInfo = new FileInfo(item.SourcePath);
            if (!sourceInfo.Exists) throw new FileNotFoundException("The source file no longer exists.", item.SourcePath);
            item.SourceSize = sourceInfo.Length;
            item.SourceModifiedUtc = sourceInfo.LastWriteTimeUtc;

            var media = await _probe.ProbeAsync(item.SourcePath, ct);

            // ---- Source track
            var requestedSource = item.Options.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : _catalog.Get(item.Options.SourceLanguage);
            // Preference: an embedded text track, then a subtitle file next to the video, then OCR of
            // an image track.
            var requestedTargets = item.Options.TargetLanguages.Select(_catalog.Find).OfType<Language>().ToList();
            var sourceStream = TrackSelection.SelectSource(media, requestedSource, item.SourceStreamIndex);
            var sidecar = sourceStream is null && item.SourceStreamIndex is null
                ? SidecarFinder.Select(SidecarFinder.Find(item.SourcePath, _catalog), requestedSource, requestedTargets)
                : null;
            var ocrStream = sourceStream is null && sidecar is null && _ocr is not null
                ? TrackSelection.SelectImageSource(media, requestedSource, _ocr.Supports, item.SourceStreamIndex)
                : null;
            if (sourceStream is null && sidecar is null && ocrStream is null)
            {
                Fail(item, DescribeMissingSource(media, requestedSource, _ocr));
                return;
            }

            // ---- Output location
            var outputMode = item.Options.OutputMode;
            var wantsMkv = outputMode is OutputMode.Mkv or OutputMode.MkvAndSubtitleFiles;
            var wantsFiles = outputMode is OutputMode.SubtitleFiles or OutputMode.MkvAndSubtitleFiles;
            var outputDir = OutputPlanner.GetOutputDirectory(item.SourcePath, item.RootPath, item.Options);
            string? outputVideo = null;
            if (wantsMkv)
            {
                outputVideo = OutputPlanner.GetOutputVideoPath(item.SourcePath, item.RootPath, item.Options);
                var isOwnPreviousOutput = item.OutputPath is not null && OutputPlanner.PathsEqual(item.OutputPath, outputVideo);
                if (File.Exists(outputVideo) && !isOwnPreviousOutput)
                {
                    switch (item.Options.ExistingOutputPolicy)
                    {
                        case ExistingOutputPolicy.Skip:
                            item.Status = JobStatus.Skipped;
                            item.Message = "Output file already exists (skipped).";
                            item.OutputPath = outputVideo;
                            item.Progress = 1;
                            return;
                        case ExistingOutputPolicy.Rename:
                            outputVideo = OutputPlanner.NextFreeName(outputVideo);
                            break;
                    }
                }
                reserved = Reserve(outputVideo, item.Id);
                outputVideo = reserved;
                if (OutputPlanner.PathsEqual(outputVideo, item.SourcePath))
                    throw new InvalidOperationException("Refusing to write the output over the source file.");
            }

            // ---- Extract and parse
            Directory.CreateDirectory(workDir);
            SubtitleDocument document;
            string sourceName;
            string? sourceTag;
            // The original text, when it is not already a track of the video, is added to the MKV too.
            var originalTracks = new List<NewSubtitleTrack>();
            if (sourceStream is not null)
            {
                sourceName = $"track #{sourceStream.Index}";
                sourceTag = sourceStream.Language;
                Report(0.02, $"Extracting subtitles (track #{sourceStream.Index}, {SubtitleCodecs.Friendly(sourceStream.CodecName)})");
                var extracted = await _extractor.ExtractAsync(media, sourceStream, workDir, ct);
                document = SubtitleParser.ParseFile(extracted);

                // Without subtitle counts in the container, a full track and e.g. an untitled
                // "signs" track look alike: compare their real number of subtitles.
                var rivals = item.SourceStreamIndex is null && sourceStream.FrameCount is null
                    ? TrackSelection.RankSources(media, requestedSource)
                        .Where(s => s.Index != sourceStream.Index && !TrackSelection.IsPartial(s) &&
                                    string.Equals(s.Language, sourceStream.Language, StringComparison.OrdinalIgnoreCase))
                        .ToList()
                    : [];
                foreach (var rival in rivals)
                {
                    var rivalDoc = SubtitleParser.ParseFile(await _extractor.ExtractAsync(media, rival, workDir, ct));
                    if (rivalDoc.Cues.Count > document.Cues.Count)
                    {
                        sourceStream = rival;
                        document = rivalDoc;
                        sourceName = $"track #{rival.Index}";
                    }
                }
            }
            else if (sidecar is not null)
            {
                sourceName = sidecar.FileName;
                sourceTag = sidecar.Language?.Code;
                Report(0.02, $"Reading {sidecar.FileName}");
                document = SubtitleParser.ParseFile(sidecar.Path);
            }
            else
            {
                var stream = ocrStream!;
                sourceName = $"track #{stream.Index} (OCR)";
                var ocrLanguage = requestedSource ?? (TrackSelection.IsUndetermined(stream.Language) ? null : _catalog.Find(stream.Language));
                if (ocrLanguage is null)
                {
                    Fail(item, $"The image subtitle track #{stream.Index} has no language tag. Select the source language explicitly so OCR knows which alphabet to read.");
                    return;
                }
                if (!_ocr!.SupportsLanguage(ocrLanguage))
                {
                    Fail(item, $"Windows has no OCR for {ocrLanguage.Name} installed. Add it in Windows Settings → Time & language → Language & region → {ocrLanguage.Name} → Language options → Optical character recognition.");
                    return;
                }
                sourceTag = ocrLanguage.Code;
                Report(0.02, $"Reading image subtitles with OCR (track #{stream.Index}, {SubtitleCodecs.Friendly(stream.CodecName)})");
                var ocrStage = $"Reading image subtitles with OCR (track #{stream.Index})";
                document = await _ocr.RecognizeAsync(media, stream, ocrLanguage, workDir,
                    new SyncProgress<double>(p => Report(0.02 + 0.03 * p, $"{ocrStage} {p:P0}")), ct);
                item.Warnings.Add($"The {ocrLanguage.Name} text was read from image subtitles with OCR; recognition errors are possible.");
            }

            var dialogue = document.Cues.Where(c => !c.IsNonDialogue && !SubtitleMarkup.Split(c.Text, document.Format).IsEmpty).ToList();
            if (dialogue.Count == 0)
            {
                Fail(item, $"The subtitles ({sourceName}) contain no dialogue text.");
                return;
            }

            // ---- Source language
            var sourceLanguage = ResolveSourceLanguage(item, requestedSource, sourceTag, document);
            if (sourceLanguage is null) return; // Fail() already called
            item.SourceLanguageDetected = sourceLanguage.Code;

            if (sourceStream is null)
            {
                var originalPath = Path.Combine(workDir, $"source.{sourceLanguage.Code}{document.DefaultExtension}");
                SubtitleWriter.WriteFile(document, originalPath);
                originalTracks.Add(new NewSubtitleTrack(originalPath, sourceLanguage,
                    ocrStream is not null ? $"{sourceLanguage.Name} (OCR)" : sourceLanguage.Name));
            }

            // ---- Target languages
            var replaced = new HashSet<int>();
            var targets = new List<Language>();
            foreach (var code in item.Options.TargetLanguages.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var target = _catalog.Find(code);
                if (target is null) { item.Warnings.Add($"Unknown language code '{code}' ignored."); continue; }
                if (target.Code == sourceLanguage.Code)
                {
                    item.SkippedLanguages.Add(target.Code);
                    item.Warnings.Add($"{target.Name} is the source language; not translated.");
                    continue;
                }
                var existing = TrackSelection.StreamsInLanguage(media, target);
                if (existing.Count > 0 && wantsMkv)
                {
                    switch (item.Options.ExistingTrackPolicy)
                    {
                        case ExistingTrackPolicy.Skip:
                            item.SkippedLanguages.Add(target.Code);
                            item.Warnings.Add($"{target.Name} subtitles already exist in the video; skipped.");
                            continue;
                        case ExistingTrackPolicy.Replace:
                            foreach (var s in existing.Where(s => s.Index != sourceStream?.Index)) replaced.Add(s.Index);
                            break;
                    }
                }
                if (!_translator.Provider.SupportsLanguage(target, asSource: false))
                {
                    item.Warnings.Add($"{_translator.Provider.DisplayName} does not support {target.Name}; skipped.");
                    item.SkippedLanguages.Add(target.Code);
                    continue;
                }
                targets.Add(target);
            }

            if (targets.Count == 0)
            {
                item.Status = JobStatus.Skipped;
                item.Message = "Nothing to translate: all selected languages are already present or not applicable.";
                item.Progress = 1;
                return;
            }

            // ---- Translate
            var glossary = _glossary?.Invoke(item) ?? Array.Empty<GlossaryEntry>();
            var newTracks = new List<NewSubtitleTrack>();
            var failed = new List<string>();
            item.SubtitleFiles.Clear();
            item.CompletedLanguages.Clear();
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = targets[i];
                var baseFraction = 0.05 + 0.85 * i / targets.Count;
                Report(baseFraction, $"Translating {sourceLanguage.Name} → {target.Name}");
                var langProgress = new SyncProgress<TranslationProgress>(p =>
                {
                    var f = p.TotalSegments == 0 ? 1 : (double)p.TranslatedSegments / p.TotalSegments;
                    Report(baseFraction + 0.85 / targets.Count * f,
                        $"Translating {sourceLanguage.Name} → {target.Name} ({p.TranslatedSegments}/{p.TotalSegments})");
                });
                try
                {
                    var result = await _translator.TranslateAsync(new DocumentTranslationRequest
                    {
                        Document = document,
                        Source = sourceLanguage,
                        Target = target,
                        Glossary = glossary,
                        Checkpoint = _pause is null ? null : _pause.WaitIfPausedAsync,
                    }, langProgress, ct);

                    item.Warnings.AddRange(result.Warnings.Select(w => $"{target.Name}: {w}"));
                    var subtitlePath = Path.Combine(workDir, $"{target.Code}{result.Document.DefaultExtension}");
                    SubtitleWriter.WriteFile(result.Document, subtitlePath);
                    newTracks.Add(new NewSubtitleTrack(subtitlePath, target, $"{target.Name} (AI)",
                        IsDefault: item.Options.MakeFirstNewTrackDefault && newTracks.Count == 0));
                    item.CompletedLanguages.Add(target.Code);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add(target.Name);
                    item.Warnings.Add($"{target.Name}: translation failed — {ex.Message}");
                }
            }

            if (newTracks.Count == 0)
            {
                Fail(item, "Translation failed for all languages. " + string.Join(" ", item.Warnings.TakeLast(3)));
                return;
            }

            // ---- Subtitle files
            if (wantsFiles)
            {
                Directory.CreateDirectory(outputDir);
                foreach (var track in ocrStream is not null ? [.. originalTracks, .. newTracks] : newTracks)
                {
                    var dest = OutputPlanner.GetSubtitleFilePath(outputDir, item.SourcePath, track.Language.Code, Path.GetExtension(track.FilePath));
                    var besideSource = OutputPlanner.PathsEqual(outputDir, Path.GetDirectoryName(item.SourcePath)!);
                    var overwrite = item.Options.ExistingOutputPolicy == ExistingOutputPolicy.Overwrite && !besideSource;
                    if (File.Exists(dest))
                    {
                        if (item.Options.ExistingOutputPolicy == ExistingOutputPolicy.Skip)
                        {
                            item.Warnings.Add($"{Path.GetFileName(dest)} already exists; not replaced.");
                            continue;
                        }
                        if (!overwrite)
                        {
                            if (besideSource && item.Options.ExistingOutputPolicy == ExistingOutputPolicy.Overwrite)
                                item.Warnings.Add($"Preserved existing source-folder subtitle {Path.GetFileName(dest)}; saved a numbered copy.");
                            dest = OutputPlanner.NextFreeName(dest);
                        }
                    }
                    var tmp = dest + $".subflow-tmp-{item.Id:N}";
                    try
                    {
                        File.Copy(track.FilePath, tmp, overwrite: false);
                        // A file created after the existence check must not be silently overwritten.
                        File.Move(tmp, dest, overwrite);
                    }
                    finally { TryDeleteFile(tmp); }
                    item.SubtitleFiles.Add(dest);
                }
            }

            // ---- Remux
            if (wantsMkv && outputVideo is not null)
            {
                Report(0.9, "Adding subtitle tracks (no re-encoding)");
                var muxer = _muxers.FirstOrDefault(m => m.CanMux(media))
                            ?? throw new InvalidOperationException("No muxer available. Install FFmpeg or MKVToolNix.");
                Directory.CreateDirectory(Path.GetDirectoryName(outputVideo)!);
                tempOutput = Path.Combine(Path.GetDirectoryName(outputVideo)!,
                    $"{Path.GetFileNameWithoutExtension(outputVideo)}.subflow-tmp-{item.Id:N}.mkv");
                var request = new MuxRequest
                {
                    Source = media,
                    OutputPath = tempOutput,
                    NewTracks = [.. originalTracks, .. newTracks],
                    ReplacedSubtitleStreams = replaced,
                    ClearOriginalDefaultSubtitle = newTracks.Any(t => t.IsDefault),
                };
                await muxer.MuxAsync(request, new SyncProgress<double>(p => Report(0.9 + 0.08 * p, "Adding subtitle tracks (no re-encoding)")), ct);

                Report(0.98, "Validating output");
                var outputInfo = await _probe.ProbeAsync(tempOutput, ct);
                OutputValidator.Validate(media, outputInfo, request);

                EnsureSourceUntouched(item);
                File.Move(tempOutput, outputVideo, overwrite: true);
                tempOutput = null;
                item.OutputPath = outputVideo;
                if (replaced.Count > 0)
                    item.Warnings.Add($"Replaced {Text.Count(replaced.Count, "original subtitle track")} as requested; the source file still contains them.");
            }
            else if (item.SubtitleFiles.Count > 0)
            {
                item.OutputPath = item.SubtitleFiles[0];
            }

            item.Status = failed.Count > 0 || item.Warnings.Count > 0 ? JobStatus.CompletedWithWarnings : JobStatus.Completed;
            item.Message = failed.Count > 0
                ? $"Completed, but failed for: {string.Join(", ", failed)}."
                : $"Added {Text.Count(newTracks.Count, "subtitle language")}: {string.Join(", ", newTracks.Select(t => t.Language.Name))}.";
            item.Progress = 1;
            item.CompletedUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            item.Status = JobStatus.Cancelled;
            item.Message = "Cancelled. Translated parts are cached; retry continues where it stopped.";
        }
        catch (Exception ex)
        {
            Fail(item, ex is MediaToolException mt && mt.Details is { Length: > 0 }
                ? $"{ex.Message} ({Truncate(mt.Details, 400)})"
                : ex.Message);
        }
        finally
        {
            if (tempOutput is not null) TryDeleteFile(tempOutput);
            if (reserved is not null) ReservedOutputs.TryRemove(reserved, out _);
            // Translated segments live in the translation cache, so the work folder is always disposable.
            TryDelete(workDir);
            item.Elapsed = sw.Elapsed;
            item.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private Language? ResolveSourceLanguage(JobItem item, Language? requested, string? sourceTag, SubtitleDocument document)
    {
        var plainLines = document.Cues.Where(c => !c.IsNonDialogue)
            .Select(c => SubtitleMarkup.ToPlain(c.Text, document.Format));
        var guess = LanguageDetector.Detect(plainLines);
        var tagged = TrackSelection.IsUndetermined(sourceTag) ? null : _catalog.Find(sourceTag);

        if (requested is not null)
        {
            if (LanguageDetector.IsReliable(guess) && guess!.Code != requested.Code && tagged?.Code != requested.Code)
                item.Warnings.Add($"Selected source language is {requested.Name}, but the text looks like {_catalog.Find(guess.Code)?.Name ?? guess.Code}.");
            return requested;
        }
        if (tagged is not null)
        {
            if (LanguageDetector.IsReliable(guess) && guess!.Code != tagged.Code)
                item.Warnings.Add($"Track is tagged {tagged.Name}, but the text looks like {_catalog.Find(guess.Code)?.Name ?? guess.Code}. Using the tag.");
            return tagged;
        }
        if (LanguageDetector.IsReliable(guess) && _catalog.Find(guess!.Code) is { } detected)
        {
            item.Warnings.Add($"Source language detected automatically as {detected.Name}.");
            return detected;
        }
        Fail(item, "The subtitle track has no language tag and the language could not be detected reliably. Select the source language explicitly.");
        return null;
    }

    private static string DescribeMissingSource(MediaInfo media, Language? requested, ISubtitleOcrEngine? ocr)
    {
        var subs = media.Subtitles.ToList();
        if (subs.Count == 0) return "The video contains no subtitle tracks and no subtitle file was found next to it.";
        var image = subs.Where(s => s.SubtitleKind == SubtitleCodecKind.Image).ToList();
        var text = subs.Where(s => s.SubtitleKind == SubtitleCodecKind.Text).ToList();
        if (text.Count == 0 && image.Count > 0)
            return $"Only image-based subtitles found ({string.Join(", ", image.Select(s => SubtitleCodecs.Friendly(s.CodecName)).Distinct())}). " +
                   (ocr is null ? "OCR is not available on this PC." : $"{ocr.Name} can read PGS tracks only.") +
                   (requested is null ? "" : $" No usable {requested.Name} image track was found.");
        if (text.Count == 0) return "No supported text subtitle tracks found.";
        var langs = string.Join(", ", text.Select(s => s.Language ?? "und").Distinct());
        return requested is null
            ? "No usable text subtitle track found."
            : $"No {requested.Name} text subtitle track found (available: {langs}).";
    }

    private static void EnsureSourceUntouched(JobItem item)
    {
        var info = new FileInfo(item.SourcePath);
        if (!info.Exists || info.Length != item.SourceSize || info.LastWriteTimeUtc != item.SourceModifiedUtc)
            throw new InvalidOperationException("The source file changed during processing. Output was discarded.");
    }

    private static string Reserve(string path, Guid id)
    {
        var candidate = path;
        for (var i = 2; ; i++)
        {
            var owner = ReservedOutputs.GetOrAdd(candidate, id);
            if (owner == id) return candidate;
            var dir = Path.GetDirectoryName(path)!;
            candidate = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)} ({i}){Path.GetExtension(path)}");
        }
    }

    private static void Fail(JobItem item, string message)
    {
        item.Status = JobStatus.Failed;
        item.Message = message;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
    }
}
