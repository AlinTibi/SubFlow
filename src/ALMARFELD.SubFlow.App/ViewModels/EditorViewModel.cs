using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Core.Abstractions;
using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Languages;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Core.Subtitles;
using ALMARFELD.SubFlow.Core.Translation;
using Microsoft.Win32;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed class EditorRow : ObservableObject
{
    private string _translation;
    private bool _isMatch;

    public EditorRow(SubtitleCue cue, string? original)
    {
        Cue = cue;
        Original = original ?? "";
        _translation = cue.Text;
        OriginalText = cue.Text;
    }

    public SubtitleCue Cue { get; }
    public int Index => Cue.Index;
    public string Time => $"{SubtitleWriter.SrtTime(Cue.Start)}\n{SubtitleWriter.SrtTime(Cue.End)}";
    public string Original { get; }
    public string OriginalText { get; private set; }
    public bool IsModified => _translation != OriginalText;
    public bool IsMatch { get => _isMatch; set => Set(ref _isMatch, value); }

    public string Translation
    {
        get => _translation;
        set { if (Set(ref _translation, value)) OnPropertyChanged(nameof(IsModified)); }
    }

    public void MarkSaved()
    {
        OriginalText = _translation;
        OnPropertyChanged(nameof(IsModified));
    }
}

/// <summary>
/// Side-by-side subtitle editor. Only text is editable; cue timing is carried over unchanged,
/// so synchronisation is preserved when saving.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly TranslationViewModel _translation;
    private SubtitleDocument? _document;
    private string? _translatedFile;       // when editing a subtitle file directly
    private MediaInfo? _video;             // when editing a track inside a video
    private MediaStream? _translatedStream;
    private EditorRow? _selected;
    private string _title = "";
    private string _search = "";
    private string _replace = "";
    private bool _matchCase;
    private string _preview = "";
    private string _status = "";
    private string _sourceLanguage = "en";
    private string _targetLanguage = "ro";
    private int _searchPosition = -1;

    public EditorViewModel(AppServices services, TranslationViewModel translation)
    {
        _services = services;
        _translation = translation;
        OpenFilesCommand = new RelayCommand(OpenFiles);
        OpenVideoCommand = new AsyncCommand(OpenVideoAsync);
        LoadTracksCommand = new AsyncCommand(LoadVideoTracksAsync, () => _video is not null && SelectedTranslatedTrack is not null);
        FindNextCommand = new RelayCommand(FindNext, () => HasDocument && Search.Length > 0);
        ReplaceAllCommand = new RelayCommand(ReplaceAll, () => HasDocument && Search.Length > 0);
        ApplyGlossaryCommand = new RelayCommand(ApplyGlossary, () => HasDocument);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => Selected is not null && Selected.Original.Length > 0);
        UsePreviewCommand = new RelayCommand(() => { if (Selected is not null) Selected.Translation = Preview; }, () => Selected is not null && Preview.Length > 0);
        SaveCommand = new RelayCommand(Save, () => HasDocument && _translatedFile is not null);
        SaveAsCommand = new RelayCommand(SaveAs, () => HasDocument);
        ExportSrtCommand = new RelayCommand(ExportSrt, () => HasDocument);
        SaveIntoVideoCommand = new AsyncCommand(SaveIntoVideoAsync, () => HasDocument && _video is not null && _translatedStream is not null);
    }

    public ObservableCollection<EditorRow> Rows { get; } = new();
    public ObservableCollection<TrackViewModel> VideoTracks { get; } = new();
    public TrackViewModel? SelectedOriginalTrack { get; set; }
    public TrackViewModel? SelectedTranslatedTrack { get; set; }
    public bool HasVideo => _video is not null;
    public bool HasDocument => _document is not null;
    public IReadOnlyList<Language> Languages => _services.Languages.All;

    public EditorRow? Selected { get => _selected; set { if (Set(ref _selected, value)) Preview = ""; } }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) _searchPosition = -1; } }
    public string Replace { get => _replace; set => Set(ref _replace, value); }
    public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value); }
    public string Preview { get => _preview; private set => Set(ref _preview, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string SourceLanguage { get => _sourceLanguage; set => Set(ref _sourceLanguage, value); }
    public string TargetLanguage
    {
        get => _targetLanguage;
        set { if (Set(ref _targetLanguage, value)) OnPropertyChanged(nameof(IsRightToLeft)); }
    }
    public bool IsRightToLeft => _services.Languages.Find(TargetLanguage)?.IsRightToLeft == true;

    public ICommand OpenFilesCommand { get; }
    public ICommand OpenVideoCommand { get; }
    public ICommand LoadTracksCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand ReplaceAllCommand { get; }
    public ICommand ApplyGlossaryCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand UsePreviewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand ExportSrtCommand { get; }
    public ICommand SaveIntoVideoCommand { get; }

    private const string SubtitleFilter = "Subtitles (*.srt;*.ass;*.ssa;*.vtt)|*.srt;*.ass;*.ssa;*.vtt|All files|*.*";

    // ---------------------------------------------------------------- loading

    private void OpenFiles()
    {
        var dlg = new OpenFileDialog { Title = L.Get("Ed_PickTranslated"), Filter = SubtitleFilter };
        if (dlg.ShowDialog() != true) return;
        var translatedPath = dlg.FileName;
        var original = new OpenFileDialog { Title = L.Get("Ed_PickOriginal"), Filter = SubtitleFilter, InitialDirectory = Path.GetDirectoryName(translatedPath) };
        var originalPath = original.ShowDialog() == true ? original.FileName : null;

        var translated = SubtitleParser.ParseFile(translatedPath);
        var originalDoc = originalPath is null ? null : SubtitleParser.ParseFile(originalPath);
        _video = null;
        _translatedStream = null;
        _translatedFile = translatedPath;
        GuessLanguagesFromFileName(translatedPath, originalPath);
        Load(translated, originalDoc, Path.GetFileName(translatedPath));
        OnPropertyChanged(nameof(HasVideo));
    }

    private async Task OpenVideoAsync()
    {
        var dlg = new OpenFileDialog { Title = L.Get("Ed_PickVideo"), Filter = "Video files|*.mkv;*.mp4;*.m4v;*.mov;*.avi;*.webm" };
        if (dlg.ShowDialog() != true) return;
        var media = await _services.Probe.ProbeAsync(dlg.FileName, CancellationToken.None);
        _video = media;
        VideoTracks.Clear();
        foreach (var t in media.Subtitles.Where(s => s.SubtitleKind == SubtitleCodecKind.Text)) VideoTracks.Add(new TrackViewModel(t));
        if (VideoTracks.Count == 0) throw new InvalidOperationException(L.Get("Ed_NoTextTracks"));
        SelectedOriginalTrack = VideoTracks.FirstOrDefault();
        SelectedTranslatedTrack = VideoTracks.LastOrDefault();
        OnPropertyChanged(nameof(SelectedOriginalTrack));
        OnPropertyChanged(nameof(SelectedTranslatedTrack));
        OnPropertyChanged(nameof(HasVideo));
        Status = L.F("Ed_VideoOpened", Path.GetFileName(media.Path), VideoTracks.Count);
        await LoadVideoTracksAsync();
    }

    private async Task LoadVideoTracksAsync()
    {
        if (_video is null || SelectedTranslatedTrack is null) return;
        var work = Path.Combine(_services.Paths.WorkDirectory, "editor");
        var translatedPath = await _services.Extractor.ExtractAsync(_video, SelectedTranslatedTrack.Stream, work, CancellationToken.None);
        var translated = SubtitleParser.ParseFile(translatedPath);
        SubtitleDocument? original = null;
        if (SelectedOriginalTrack is not null && SelectedOriginalTrack.Index != SelectedTranslatedTrack.Index)
            original = SubtitleParser.ParseFile(await _services.Extractor.ExtractAsync(_video, SelectedOriginalTrack.Stream, work, CancellationToken.None));
        _translatedStream = SelectedTranslatedTrack.Stream;
        _translatedFile = null;
        SourceLanguage = _services.Languages.Find(SelectedOriginalTrack?.Stream.Language)?.Code ?? SourceLanguage;
        TargetLanguage = _services.Languages.Find(SelectedTranslatedTrack.Stream.Language)?.Code ?? TargetLanguage;
        Load(translated, original, $"{Path.GetFileName(_video.Path)} · #{SelectedTranslatedTrack.Index} {SelectedTranslatedTrack.Language}");
    }

    private void Load(SubtitleDocument translated, SubtitleDocument? original, string title)
    {
        _document = translated;
        Rows.Clear();
        var aligned = Align(translated, original);
        foreach (var cue in translated.Cues)
            Rows.Add(new EditorRow(cue, aligned.TryGetValue(cue.Index, out var o) ? o : null));
        Title = title;
        Status = L.F("Ed_Loaded", Rows.Count);
        OnPropertyChanged(nameof(HasDocument));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Pairs translated cues with original cues: by position when counts match, otherwise by start time.</summary>
    private static Dictionary<int, string> Align(SubtitleDocument translated, SubtitleDocument? original)
    {
        var result = new Dictionary<int, string>();
        if (original is null) return result;
        if (original.Cues.Count == translated.Cues.Count)
        {
            for (var i = 0; i < translated.Cues.Count; i++)
                result[translated.Cues[i].Index] = SubtitleMarkup.ToPlain(original.Cues[i].Text, original.Format);
            return result;
        }
        var byStart = original.Cues.GroupBy(c => (long)Math.Round(c.Start.TotalMilliseconds / 50)).ToDictionary(g => g.Key, g => g.First());
        foreach (var cue in translated.Cues)
            if (byStart.TryGetValue((long)Math.Round(cue.Start.TotalMilliseconds / 50), out var o))
                result[cue.Index] = SubtitleMarkup.ToPlain(o.Text, original.Format);
        return result;
    }

    private void GuessLanguagesFromFileName(string translated, string? original)
    {
        // "Episode 01.ro.srt" → ro
        static string? Code(string? path) => path is null ? null : Path.GetExtension(Path.GetFileNameWithoutExtension(path)).TrimStart('.');
        if (_services.Languages.Find(Code(translated)) is { } t) TargetLanguage = t.Code;
        if (_services.Languages.Find(Code(original)) is { } s) SourceLanguage = s.Code;
    }

    // ---------------------------------------------------------------- editing tools

    private StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void FindNext()
    {
        foreach (var r in Rows) r.IsMatch = r.Translation.Contains(Search, Comparison) || r.Original.Contains(Search, Comparison);
        for (var step = 1; step <= Rows.Count; step++)
        {
            var i = (_searchPosition + step) % Rows.Count;
            if (Rows[i].IsMatch)
            {
                _searchPosition = i;
                Selected = Rows[i];
                Status = L.F("Ed_Matches", Rows.Count(r => r.IsMatch));
                return;
            }
        }
        Status = L.Get("Ed_NoMatches");
    }

    private void ReplaceAll()
    {
        var count = 0;
        foreach (var r in Rows)
        {
            if (!r.Translation.Contains(Search, Comparison)) continue;
            r.Translation = r.Translation.Replace(Search, Replace, Comparison);
            count++;
        }
        Status = L.F("Ed_Replaced", count);
    }

    private void ApplyGlossary()
    {
        var entries = GlossaryService.Merge(_services.GlobalGlossary.Entries);
        var count = 0;
        foreach (var r in Rows)
        {
            var updated = GlossaryService.Apply(r.Translation, entries, TargetLanguage);
            if (updated == r.Translation) continue;
            r.Translation = updated;
            count++;
        }
        Status = L.F("Ed_GlossaryApplied", count);
    }

    private async Task PreviewAsync()
    {
        if (Selected is null) return;
        var provider = _services.CreateProvider(_translation.EngineId);
        if (provider.SendsTextOffMachine && !_services.Settings.CloudConsentGivenThisSession)
        {
            if (MessageBox.Show(Application.Current.MainWindow!, L.F("Tr_CloudConsent", provider.DisplayName), L.Get("Tr_CloudConsentTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _services.Settings.CloudConsentGivenThisSession = true;
        }
        Status = L.Get("Ed_Translating");
        var source = _services.Languages.Get(SourceLanguage);
        var target = _services.Languages.Get(TargetLanguage);
        var doc = new SubtitleDocument(SubtitleFormat.Srt, [new SubtitleCue(1, Selected.Cue.Start, Selected.Cue.End, Selected.Original)]);
        var result = await _services.CreateTranslator(provider).TranslateAsync(new DocumentTranslationRequest
        {
            Document = doc, Source = source, Target = target, Glossary = _services.GlobalGlossary.Entries,
        }, null, CancellationToken.None);
        Preview = result.Document.Cues[0].Text;
        Status = L.Get("Ed_PreviewReady");
    }

    // ---------------------------------------------------------------- saving

    private SubtitleDocument BuildDocument() =>
        _document!.WithTexts(Rows.ToDictionary(r => r.Index, r => r.Translation.Replace("\r\n", "\n")));

    private void Save()
    {
        if (_translatedFile is null) { SaveAs(); return; }
        SubtitleWriter.WriteFile(BuildDocument(), _translatedFile);
        foreach (var r in Rows) r.MarkSaved();
        Status = L.F("Ed_Saved", Path.GetFileName(_translatedFile));
    }

    private void SaveAs()
    {
        var doc = BuildDocument();
        var dlg = new SaveFileDialog
        {
            Filter = SubtitleFilter,
            FileName = (_video is not null ? Path.GetFileNameWithoutExtension(_video.Path) + "." + TargetLanguage : Path.GetFileNameWithoutExtension(_translatedFile ?? "subtitles")) + doc.DefaultExtension,
        };
        if (dlg.ShowDialog() != true) return;
        SubtitleWriter.WriteFile(doc, dlg.FileName);
        _translatedFile = dlg.FileName;
        foreach (var r in Rows) r.MarkSaved();
        Status = L.F("Ed_Saved", Path.GetFileName(dlg.FileName));
    }

    private void ExportSrt()
    {
        var dlg = new SaveFileDialog { Filter = "SubRip (*.srt)|*.srt", FileName = Path.GetFileNameWithoutExtension(_translatedFile ?? _video?.Path ?? "subtitles") + ".srt" };
        if (dlg.ShowDialog() != true) return;
        SubtitleWriter.WriteFile(BuildDocument().ToSrt(), dlg.FileName);
        Status = L.F("Ed_Saved", Path.GetFileName(dlg.FileName));
    }

    /// <summary>Writes a new MKV where the edited track replaces the original one. The opened video is never modified.</summary>
    private async Task SaveIntoVideoAsync()
    {
        if (_video is null || _translatedStream is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Matroska video (*.mkv)|*.mkv",
            FileName = Path.GetFileNameWithoutExtension(_video.Path) + ".edited.mkv",
            InitialDirectory = Path.GetDirectoryName(_video.Path),
        };
        if (dlg.ShowDialog() != true) return;
        if (OutputPlanner.PathsEqual(dlg.FileName, _video.Path))
            throw new InvalidOperationException(L.Get("Ed_NoOverwriteSource"));

        Status = L.Get("Ed_Remuxing");
        var work = Path.Combine(_services.Paths.WorkDirectory, "editor");
        Directory.CreateDirectory(work);
        var doc = BuildDocument();
        var subtitlePath = Path.Combine(work, "edited" + doc.DefaultExtension);
        SubtitleWriter.WriteFile(doc, subtitlePath);

        var language = _services.Languages.Get(TargetLanguage);
        var title = _translatedStream.Title ?? $"{language.Name}";
        var temp = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, $"{Path.GetFileNameWithoutExtension(dlg.FileName)}.subflow-tmp-{Guid.NewGuid():N}.mkv");
        var request = new MuxRequest
        {
            Source = _video,
            OutputPath = temp,
            NewTracks = [new NewSubtitleTrack(subtitlePath, language, title, _translatedStream.IsDefault)],
            ReplacedSubtitleStreams = [_translatedStream.Index],
            ClearOriginalDefaultSubtitle = _translatedStream.IsDefault,
        };
        try
        {
            var muxer = _services.Muxers.First(m => m.CanMux(_video));
            await muxer.MuxAsync(request, null, CancellationToken.None);
            OutputValidator.Validate(_video, await _services.Probe.ProbeAsync(temp, CancellationToken.None), request);
            File.Move(temp, dlg.FileName, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        foreach (var r in Rows) r.MarkSaved();
        Status = L.F("Ed_Saved", Path.GetFileName(dlg.FileName));
    }
}
