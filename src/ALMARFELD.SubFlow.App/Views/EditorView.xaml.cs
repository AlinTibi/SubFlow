using System.Windows.Controls;

namespace ALMARFELD.SubFlow.App.Views;

public partial class EditorView : UserControl
{
    public EditorView() => InitializeComponent();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is { } item) Grid.ScrollIntoView(item);
    }
}
