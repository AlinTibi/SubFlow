using System.Windows.Controls;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Views;

public partial class VideosView : UserControl
{
    public VideosView() => InitializeComponent();

    /// <summary>Delete removes the selected videos from the list (the files on disk are not touched).</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not VideosViewModel vm) return;
        if (vm.RemoveCommand.CanExecute(List.SelectedItems)) vm.RemoveCommand.Execute(List.SelectedItems);
        e.Handled = true;
    }
}
