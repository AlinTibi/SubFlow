using System.Windows;
using System.Windows.Controls;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old) old.KeysSaved -= ClearKeys;
            if (e.NewValue is SettingsViewModel vm) vm.KeysSaved += ClearKeys;
        };
        Unloaded += (_, _) => { if (DataContext is SettingsViewModel vm) vm.KeysSaved -= ClearKeys; };
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    // PasswordBox.Password is intentionally not bindable; keys are pushed to the view model only on change.
    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { Tag: string provider } box || Vm is not { } vm) return;
        switch (provider)
        {
            case "openai": vm.NewOpenAiKey = box.Password; break;
            case "deepl": vm.NewDeepLKey = box.Password; break;
            case "google": vm.NewGoogleKey = box.Password; break;
        }
    }

    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string provider }) Vm?.RemoveKey(provider);
    }

    private void ClearKeys()
    {
        OpenAiKey.Clear();
        DeepLKey.Clear();
        GoogleKey.Clear();
    }
}
