using System.Windows;
namespace MXSimModManager.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Contains("--background-test")) { ShowActivated = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -10000; Top = -10000; }
        viewModel = new MainViewModel(this);
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            if (Environment.GetCommandLineArgs().Contains("--appearance")) viewModel.AppearanceCommand.Execute(null);
            if (Environment.GetCommandLineArgs().Contains("--forum")) viewModel.ForumCommand.Execute(null);
        };
        Closing += (_, e) => { if (viewModel.IsBusy) { MessageBox.Show(this, "Please wait for the current operation to finish before closing.", "Operation in progress"); e.Cancel = true; } };
        Closed += (_, _) => viewModel.Dispose();
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    { e.Effects = !viewModel.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!viewModel.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await viewModel.InstallPathsAsync(paths);
    }
    private void LibraryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (DataContext is MainViewModel vm) vm.SetSelection(LibraryGrid.SelectedItems.OfType<MXSimModManager.Core.ModPackage>()); }
}
