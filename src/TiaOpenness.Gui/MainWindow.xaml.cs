using System.Windows;
using System.Windows.Controls;
using TiaOpenness.Gui.ViewModels;

namespace TiaOpenness.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _model;
        Closed += (_, _) => _model.Dispose();
        Loaded += async (_, _) =>
        {
            SelectStartupTab(System.Environment.GetCommandLineArgs());
            await _model.ApplyStartupAsync(System.Environment.GetCommandLineArgs());
        };
    }

    /// <summary>Honours --tab blocks|vc so the app can be launched straight into a view.</summary>
    private void SelectStartupTab(string[] args)
    {
        var index = System.Array.FindIndex(args, a =>
            string.Equals(a, "--tab", System.StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length) return;

        Tabs.SelectedIndex = args[index + 1].ToLowerInvariant() switch
        {
            "vc" or "version-control" => 1,
            _ => 0,
        };
    }

    // ---- window buttons ----------------------------------------------------
    // WindowStyle=None removed the system caption, so close, minimise and zoom are ours to
    // implement. Dragging, double-click-to-zoom and Aero Snap still come from WindowChrome.

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnZoom(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---- browse buttons ----------------------------------------------------

    private void OnBrowseProject(object sender, RoutedEventArgs e) => _model.BrowseProject();

    private void OnBrowseOutput(object sender, RoutedEventArgs e) => _model.BrowseOutput();

    private void OnBrowseWorkspace(object sender, RoutedEventArgs e) => _model.BrowseWorkspaceFolder();

    private void OnSelectAll(object sender, RoutedEventArgs e) => _model.SelectAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => _model.SelectAll(false);

    // ---- log ---------------------------------------------------------------

    /// <summary>Keeps the log pinned to the newest line as it grows.</summary>
    private void OnLogChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) box.ScrollToEnd();
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_model.Log);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is a shared OS resource and another process can hold it open.
            // Failing to copy a log is not worth an error dialog.
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _model.ClearLog();
}
