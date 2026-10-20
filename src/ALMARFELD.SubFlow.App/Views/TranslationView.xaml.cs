using System.Windows;
using System.Windows.Controls;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Views;

public partial class TranslationView : UserControl
{
    public TranslationView() => InitializeComponent();

    private TranslationViewModel? Vm => DataContext as TranslationViewModel;

    private void OnEngineLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id } rb && Vm is { } vm) rb.IsChecked = vm.EngineId == id;
    }

    private void OnEngineChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string id } && Vm is { } vm) vm.EngineId = id;
    }
}
