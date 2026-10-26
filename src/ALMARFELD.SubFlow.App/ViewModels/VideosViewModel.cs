using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Core.Media;
using ALMARFELD.SubFlow.Core.Processing;
using ALMARFELD.SubFlow.Core.Subtitles;
using Microsoft.Win32;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed class TrackViewModel(MediaStream stream, bool ocrReadable = false)
{
    public MediaStream Stream { get; } = stream;
    public int Index => Stream.Index;
    public string Ref => $"#{Stream.Index}";
    public string Codec => SubtitleCodecs.Friendly(Stream.CodecName);
    public string Language => string.IsNullOrWhiteSpace(Stream.Language) ? "und" : Stream.Language!;
    public string Title => Stream.Title ?? "";
    public bool IsText => Stream.SubtitleKind == SubtitleCodecKind.Text;
    public bool IsUsable => IsText || ocrReadable;
    public string Support => IsText ? L.Get("Track_Translatable")
        : ocrReadable ? L.Get("Track_ImageOcr")
        : Stream.SubtitleKind == SubtitleCodecKind.Image ? L.Get("Track_ImageUnsupported")
        : L.Get("Track_Unsupported");
    public string Flags => string.Join(" ", new[] { Stream.IsDefault ? "default" : null, Stream.IsForced ? "forced" : null, Stream.IsHearingImpaired ? "SDH" : null }.Where(x => x is not null));
}

/// <summary>A subtitle file next to the video; shown with the same template as embedded tracks.</summary>
public sealed class SidecarTrackViewModel(SidecarSubtitle sidecar)
{
    public SidecarSubtitle Sidecar { get; } = sidecar;
    public string Ref => L.Get("Track_File");
    public string Codec => Sidecar.Extension;
    public string Language => Sidecar.Language?.Iso6392B ?? "und";
    public string Title => Sidecar.FileName;
    public bool IsUsable => true;
    public string Support => L.Get("Track_Translatable");
    public string Flags => string.Join(" ", new[] { Sidecar.IsForced ? "forced" : null, Sidecar.IsHearingImpaired ? "SDH" : null }.Where(x => x is not null));
}

public sealed class VideoEntry : ObservableObject
{
    private string _state = L.Get("Video_Scanning");
    private string _summary = "";
    private bool _isReady;
    private bool _hasError;
    private IReadOnlyList<object> _tracks = Array.Empty<object>();

    public VideoEntry(string path, string root)
    {
        Path = path;
        Root = root;
    }

    public string Path { get; }
    public string Root { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public MediaInfo? Media { get; private set; }

    public string State { get => _state; private set => Set(ref _state, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsReady { get => _isReady; private set => Set(ref _isReady, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    /// <summary>Embedded tracks (<see cref="TrackViewModel"/>) followed by subtitle files next to the video.</summary>
    public IReadOnlyList<object> Tracks { get => _tracks; private set => Set(ref _tracks, value); }
    /// <summary>A text track, a subtitle file or an OCR-readable image track is available.</summary>
    public bool CanTranslate { get; private set; }
    public string Info => Media is null ? "" :
        $"{(Media.Duration is { } d ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : "?")} · {Media.SizeBytes / 1024.0 / 1024.0:0.#} MB · {Media.FormatName.Split(',')[0]}";

    public void SetProbed(MediaInfo media, IReadOnlyList<SidecarSubtitle> sidecars, Func<MediaStream, bool> ocrReadable)
    {
        Media = media;
        var embedded = media.Subtitles.Select(s => new TrackViewModel(s, s.SubtitleKind == SubtitleCodecKind.Image && ocrReadable(s))).ToList();
        var files = sidecars.Select(s => new SidecarTrackViewModel(s)).ToList();
        Tracks = [.. embedded, .. files];
        Summary = Tracks.Count == 0
            ? L.Get("Video_NoSubs")
            : string.Join("  ·  ", embedded.Select(t => $"{t.Language} {t.Codec}").Concat(files.Select(f => $"{f.Language} {f.Codec} ({L.Get("Track_File")})")));
        var hasText = embedded.Any(t => t.IsText);
        var hasUsable = embedded.Any(t => t.IsUsable);
        State = hasText ? L.Get("Video_Ready")
            : files.Count > 0 ? L.Get("Video_ReadySidecar")
            : hasUsable ? L.Get("Video_ReadyOcr")
            : Tracks.Count > 0 ? L.Get("Video_ImageOnly")
            : L.Get("Video_NoSubsShort");
        CanTranslate = hasUsable || files.Count > 0;
        HasError = !CanTranslate;
        IsReady = true;
        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(Info));
    }

    public void SetError(string message)
    {
        State = L.Get("Video_Unreadable");
        Summary = message;
        HasError = true;
        IsReady = true;
    }
}

/// <summary>"Videos" page: import by drag &amp; drop or dialogs, probes every file's tracks in the background.</summary>
public sealed class VideosViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly SemaphoreSlim _probeGate = new(4);
    private VideoEntry? _selected;
    private bool _isScanning;

    public VideosViewModel(AppServices services)
    {
        _services = services;
        AddFilesCommand = new RelayCommand(AddFiles);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveCommand = new RelayCommand(p => Remove(p), _ => Selected is not null);
        ClearCommand = new RelayCommand(() => { Entries.Clear(); Notify(); }, () => Entries.Count > 0);
        Entries.CollectionChanged += (_, _) => Notify();
    }

    public ObservableCollection<VideoEntry> Entries { get; } = new();
    public VideoEntry? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearCommand { get; }

    public int Count => Entries.Count;
    public int ReadyCount => Entries.Count(e => e.IsReady && e.CanTranslate);
    public int ProblemCount => Entries.Count(e => e.IsReady && !e.CanTranslate);
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>Languages found in text subtitle tracks across all videos (e.g. "eng ×120").</summary>
    public string LanguagesSummary => string.Join(", ", Entries.SelectMany(e => e.Tracks.OfType<TrackViewModel>().Where(t => t.IsText))
        .GroupBy(t => t.Language).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}"));

    public event Action? Changed;

    private void Notify()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LanguagesSummary));
        Changed?.Invoke();
    }

    private void AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = L.Get("Videos_AddFiles"),
            Multiselect = true,
            Filter = "Video files|*.mkv;*.mp4;*.m4v;*.mov;*.avi;*.webm|All files|*.*",
        };
        if (dlg.ShowDialog() == true) _ = AddPathsAsync(dlg.FileNames);
    }

    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.Get("Videos_AddFolder"), Multiselect = true };
        if (dlg.ShowDialog() == true) _ = AddPathsAsync(dlg.FolderNames);
    }

    private void Remove(object? parameter)
    {
        var items = parameter is System.Collections.IList list
            ? list.OfType<VideoEntry>().ToList()
            : Selected is null ? new List<VideoEntry>() : [Selected];
        foreach (var e in items) Entries.Remove(e);
        if (Selected is not null && !Entries.Contains(Selected)) Selected = Entries.FirstOrDefault();
        Notify();
    }

    /// <summary>Adds files and folders (recursively) and probes new entries.</summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        IsScanning = true;
        try
        {
            var recursive = _services.Settings.RecursiveScan;
            var input = paths.ToList();
            var found = await Task.Run(() => OutputPlanner.ExpandInputs(input, recursive).ToList());
            var existing = new HashSet<string>(Entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            var added = new List<VideoEntry>();
            foreach (var (file, root) in found)
            {
                if (!existing.Add(file)) continue;
                var entry = new VideoEntry(file, root);
                Entries.Add(entry);
                added.Add(entry);
            }
            await Task.WhenAll(added.Select(ProbeAsync));
        }
        finally
        {
            IsScanning = false;
            Notify();
        }
    }

    private async Task ProbeAsync(VideoEntry entry)
    {
        await _probeGate.WaitAsync();
        try
        {
            var media = await _services.Probe.ProbeAsync(entry.Path, CancellationToken.None);
            var sidecars = await Task.Run(() => SidecarFinder.Find(entry.Path, _services.Languages));
            entry.SetProbed(media, sidecars, _services.Ocr.Supports);
        }
        catch (Exception ex)
        {
            entry.SetError(ex.Message);
        }
        finally
        {
            _probeGate.Release();
            Ui.Run(Notify);
        }
    }
}
