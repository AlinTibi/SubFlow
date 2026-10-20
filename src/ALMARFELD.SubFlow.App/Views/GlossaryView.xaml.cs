using System.Windows;
using System.Windows.Controls;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Views;

public partial class GlossaryView : UserControl
{
    public GlossaryView() => InitializeComponent();

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => MarkDirty();
    private void OnChanged(object sender, RoutedEventArgs e) => MarkDirty();
    private void MarkDirty() { if (DataContext is GlossaryViewModel vm) vm.Dirty = true; }
}
