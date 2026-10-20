using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Core.Jobs;
using Microsoft.Win32;

namespace ALMARFELD.SubFlow.App.ViewModels;

public sealed class HistoryEntry(JobItem item)
{
    public JobItem Item { get; } = item;
    public string FileName => Path.GetFileName(Item.SourcePath);
    public JobStatus Status => Item.Status;
    public string When => Item.UpdatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Languages => Item.CompletedLanguages.Count > 0
        ? string.Join(", ", Item.CompletedLanguages.Select(c => c.ToUpperInvariant()))
        : string.Join(", ", Item.Options.TargetLanguages.Select(c => c.ToUpperInvariant()));
    public string Engine => Item.EngineDescription ?? Item.Options.ProviderId;
    public string Duration => Item.Elapsed is { } e ? e.ToString(e.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture) : "";
    public string Message => Item.Message ?? "";
    public string Details => string.Join("\n", new[] { Item.Message }.Concat(Item.Warnings).Where(s => !string.IsNullOrWhiteSpace(s)));
    public string? OutputPath => Item.OutputPath;
}

/// <summary>Processing history from the local database, with a CSV report export.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private HistoryEntry? _selected;

    public HistoryViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new RelayCommand(Refresh);
        ClearCommand = new RelayCommand(Clear, () => Entries.Count > 0);
        ExportCommand = new RelayCommand(Export, () => Entries.Count > 0);
        OpenOutputCommand = new RelayCommand(p => Shell.ShowInExplorer((p as HistoryEntry ?? Selected)?.OutputPath));
        Refresh();
    }

    public ObservableCollection<HistoryEntry> Entries { get; } = new();
    public HistoryEntry? Selected { get => _selected; set => Set(ref _selected, value); }
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public bool IsEmpty => Entries.Count == 0;

    public void Refresh()
    {
        Entries.Clear();
        foreach (var item in _services.Store.GetHistory()) Entries.Add(new HistoryEntry(item));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Clear()
    {
        if (MessageBox.Show(Application.Current.MainWindow!, L.Get("Hist_ClearConfirm"), "ALMARFELD SubFlow",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _services.Store.ClearHistory();
        Refresh();
    }

    private void Export()
    {
        var dlg = new SaveFileDialog { Filter = "CSV report|*.csv", FileName = $"SubFlow report {DateTime.Now:yyyy-MM-dd}.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("file,status,languages,engine,duration,output,message\r\n");
        static string Csv(string? v) => v is null ? "" : "\"" + v.Replace("\"", "\"\"") + "\"";
        foreach (var e in Entries)
            sb.Append(Csv(e.Item.SourcePath)).Append(',').Append(e.Status).Append(',').Append(Csv(e.Languages)).Append(',')
              .Append(Csv(e.Engine)).Append(',').Append(Csv(e.Duration)).Append(',').Append(Csv(e.OutputPath)).Append(',')
              .Append(Csv(e.Details.Replace("\n", " | "))).Append("\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }
}
