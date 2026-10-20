using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.Mvvm;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.Core.Glossary;
using Microsoft.Win32;

namespace ALMARFELD.SubFlow.App.ViewModels;

/// <summary>
/// Glossary page. "Global" applies to every job; a "project" glossary is a subflow.glossary.json file
/// in an import folder and overrides global entries for videos imported from that folder.
/// </summary>
public sealed class GlossaryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string? _projectFolder;
    private bool _dirty;

    public GlossaryViewModel(AppServices services)
    {
        _services = services;
        LoadGlobal();
        AddCommand = new RelayCommand(() => { Entries.Add(new GlossaryEntry { Source = "", Protected = true }); Dirty = true; });
        RemoveCommand = new RelayCommand(p => { if (p is GlossaryEntry e) { Entries.Remove(e); Dirty = true; } });
        SaveCommand = new RelayCommand(Save);
        ImportCommand = new RelayCommand(Import);
        ExportCommand = new RelayCommand(Export);
        UseGlobalCommand = new RelayCommand(() => { if (ConfirmDiscard()) { _projectFolder = null; LoadGlobal(); Notify(); } });
        OpenProjectCommand = new RelayCommand(OpenProject);
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Count));
    }

    public ObservableCollection<GlossaryEntry> Entries { get; } = new();
    public int Count => Entries.Count;
    public bool IsProject => _projectFolder is not null;
    public string Scope => _projectFolder is null ? L.Get("Gl_ScopeGlobal") : L.F("Gl_ScopeProject", _projectFolder);
    public bool Dirty { get => _dirty; set => Set(ref _dirty, value); }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand UseGlobalCommand { get; }
    public ICommand OpenProjectCommand { get; }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsProject));
        OnPropertyChanged(nameof(Scope));
        Dirty = false;
    }

    private void LoadGlobal()
    {
        Entries.Clear();
        foreach (var e in _services.GlobalGlossary.Entries) Entries.Add(e);
    }

    private void OpenProject()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFolderDialog { Title = L.Get("Gl_ChooseProject") };
        if (dlg.ShowDialog() != true) return;
        _projectFolder = dlg.FolderName;
        Entries.Clear();
        foreach (var e in GlossaryService.Load(AppServices.ProjectGlossaryPath(_projectFolder)).Entries) Entries.Add(e);
        Notify();
    }

    private bool ConfirmDiscard() => !Dirty ||
        MessageBox.Show(Application.Current.MainWindow!, L.Get("Gl_Discard"), "ALMARFELD SubFlow", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Save()
    {
        var doc = new GlossaryDocument
        {
            Name = _projectFolder is null ? "Global" : Path.GetFileName(_projectFolder),
            Entries = Entries.Where(e => !string.IsNullOrWhiteSpace(e.Source))
                .Select(e => { e.Source = e.Source.Trim(); e.Target = string.IsNullOrWhiteSpace(e.Target) ? null : e.Target.Trim();
                               e.TargetLanguage = string.IsNullOrWhiteSpace(e.TargetLanguage) ? null : e.TargetLanguage.Trim().ToLowerInvariant(); return e; })
                .ToList(),
        };
        if (_projectFolder is null) _services.SaveGlobalGlossary(doc);
        else GlossaryService.Save(doc, AppServices.ProjectGlossaryPath(_projectFolder));
        Entries.Clear();
        foreach (var e in doc.Entries) Entries.Add(e);
        Dirty = false;
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Glossary (*.json;*.csv)|*.json;*.csv" };
        if (dlg.ShowDialog() != true) return;
        var imported = GlossaryService.Load(dlg.FileName).Entries;
        var merged = GlossaryService.Merge(Entries, imported);
        Entries.Clear();
        foreach (var e in merged) Entries.Add(e);
        Dirty = true;
    }

    private void Export()
    {
        var dlg = new SaveFileDialog { Filter = "JSON|*.json|CSV|*.csv", FileName = "subflow-glossary.json" };
        if (dlg.ShowDialog() != true) return;
        GlossaryService.Save(new GlossaryDocument { Name = "Export", Entries = Entries.ToList() }, dlg.FileName);
    }
}
