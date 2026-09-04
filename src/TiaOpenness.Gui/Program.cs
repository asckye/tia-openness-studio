using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TiaOpenness.Gui;

/// <summary>What the single executable was asked to be this time.</summary>
public enum AppMode
{
    /// <summary>The desktop window. What a double-click gets.</summary>
    Desktop,
    /// <summary>A Model Context Protocol server on stdio, for an AI client.</summary>
    Mcp,
}

/// <summary>
/// The one entry point. The desktop app and the MCP server used to be separate executables, each
/// carrying its own copy of the .NET runtime; they are one now, chosen by the first argument.
///
/// The assembly is a WinExe so a double-click does not flash a console window, which means the
/// MCP mode has to find its own console when it needs one - see <see cref="AttachConsole()"/>.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return Classify(args) == AppMode.Mcp
            ? RunMcp(args.Skip(1).ToArray())
            : RunDesktop();
    }

    /// <summary>
    /// Picks the mode from the first argument. Everything else - including every switch the
    /// desktop app takes, such as <c>--mock</c> or <c>--project</c> - opens the window.
    /// </summary>
    public static AppMode Classify(string[] args)
    {
        return args.Length > 0 && string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase)
            ? AppMode.Mcp
            : AppMode.Desktop;
    }

    private static int RunDesktop()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunMcp(string[] args)
    {
        AttachConsole();
        return Mcp.Program.RunAsync(args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gives the MCP mode somewhere to write when it was not handed pipes.
    ///
    /// An MCP client always redirects stdio, and in that case this does nothing at all: attaching
    /// a console would take the protocol stream away from the client. The rest is for a person
    /// running <c>TiaOpenness.exe mcp</c> by hand to see the diagnostics - borrowing the terminal's
    /// console, or allocating one so a mistaken double-click is not silently inert.
    /// </summary>
    private static void AttachConsole()
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return;

        if (!AttachConsole(AttachParentProcess) && !AllocConsole()) return;

        // Streams cached before the console existed still point nowhere.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
