using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TiaOpenness.Gui;

public partial class App : Application
{
    /// <summary>Where the crash log lands, so a field failure can be sent back as one file.</summary>
    public static string CrashLogPath { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? Path.GetTempPath(),
        "TiaOpenness.Studio.crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // A background failure that reaches the dispatcher would otherwise kill the app
        // silently while a TIA session is still open.
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"{DateTimeOffset.Now:O}{System.Environment.NewLine}{e.Exception}{System.Environment.NewLine}{System.Environment.NewLine}");
        }
        catch (Exception)
        {
            // Reporting the original failure matters more than logging it.
        }

        MessageBox.Show(
            e.Exception.Message + System.Environment.NewLine + System.Environment.NewLine +
            "Details written to " + CrashLogPath,
            "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
