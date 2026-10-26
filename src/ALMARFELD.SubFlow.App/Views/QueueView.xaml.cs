using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Views;

public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();

    private QueueViewModel? Vm => DataContext as QueueViewModel;

    private List<JobItemViewModel> SelectedRows() => List.SelectedItems.OfType<JobItemViewModel>().ToList();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm is null) return;
        Vm.Remove(SelectedRows());
        e.Handled = true;
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e) => Vm?.Remove(SelectedRows());

    private void OnRetrySelected(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        foreach (var row in SelectedRows())
            if (Vm.RetryCommand.CanExecute(row)) Vm.RetryCommand.Execute(row);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && SelectedRows().FirstOrDefault() is { } row && Vm.OpenOutputCommand.CanExecute(row))
            Vm.OpenOutputCommand.Execute(row);
    }
}
