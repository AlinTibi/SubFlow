using System.Windows.Controls;
using System.Windows.Input;
using ALMARFELD.SubFlow.App.ViewModels;

namespace ALMARFELD.SubFlow.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    /// <summary>The "Files waiting" card opens the queue.</summary>
    private void OnWaitingClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DashboardViewModel vm && vm.GoCommand.CanExecute("Queue")) vm.GoCommand.Execute("Queue");
    }
}
