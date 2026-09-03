using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>A block row with the selection state the export list needs.</summary>
public sealed class BlockRow(BlockInfo info) : ObservableObject
{
    private bool _selected;

    public BlockInfo Info { get; } = info;
    public string Path => Info.Path;
    public string Name => Info.Name;
    public string Kind => Info.Kind.ToString();
    public string Number => Info.Number?.ToString() ?? "-";
    public string Language => Info.ProgrammingLanguage ?? "-";
    public string Author => Info.HeaderAuthor ?? "-";

    /// <summary>Short reason the block cannot be exported, or empty when it can.</summary>
    public string Status =>
        Info.IsKnowHowProtected ? "know-how protected"
        : !Info.IsConsistent ? "needs compiling"
        : string.Empty;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

/// <summary>Drives the single main window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly TiaClient _client = new();
    private bool _started;

    private string _projectPath = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _status = "Not connected.";
    private string _log = string.Empty;
    private string _blockFilter = string.Empty;
    private string _namePattern = "^(OB|FB|FC|DB|UDT)_";
    private DeviceInfo? _selectedDevice;
    private bool _useMock;
    private bool _headless;
    private bool _sourceFormat;
    private bool _busy;
    private int _progressValue;
    private int _progressMax;

    private bool _vcSupported;
    private bool _vcDryRun = true;
    private bool _vcShowAll;
    private WorkspaceInfo? _selectedWorkspace;
    private string _newWorkspaceName = "git";
    private string _newWorkspaceFolder = string.Empty;

    public MainViewModel()
    {
        Blocks = new ObservableCollection<BlockRow>();
        BlocksView = CollectionViewSource.GetDefaultView(Blocks);
        BlocksView.Filter = FilterBlock;

        _client.Bridge.Log += (_, e) => Append("bridge: " + e.Line);
        _client.Bridge.Progress += (_, e) => OnProgress(e.Progress);
        _client.Bridge.Exited += (_, _) => Append("bridge process exited.");

        RunDoctor = new AsyncCommand(DoctorAsync);
        Connect = new AsyncCommand(ConnectAsync);
        OpenProject = new AsyncCommand(OpenProjectAsync, () => ProjectPath.Length > 0);
        Refresh = new AsyncCommand(RefreshBlocksAsync, () => SelectedDevice is not null);
        Export = new AsyncCommand(ExportAsync, () => SelectedDevice is not null && OutputDirectory.Length > 0);
        Import = new AsyncCommand(ImportAsync, () => SelectedDevice is not null);
        Compile = new AsyncCommand(CompileAsync, () => SelectedDevice is not null);
        Inspect = new AsyncCommand(InspectAsync, () => SelectedDevice is not null);
        Save = new AsyncCommand(SaveAsync, () => SelectedDevice is not null);

        VcRefresh = new AsyncCommand(VcRefreshAsync);
        VcCreate = new AsyncCommand(VcCreateAsync,
            () => VcSupported && NewWorkspaceName.Length > 0 && NewWorkspaceFolder.Length > 0);
        VcMap = new AsyncCommand(VcMapAsync, () => VcSupported && SelectedWorkspace is not null);
        VcStatus = new AsyncCommand(VcStatusAsync, () => VcSupported && SelectedWorkspace is not null);
        VcPush = new AsyncCommand(VcPushAsync, () => VcSupported && SelectedWorkspace is not null);
        VcPull = new AsyncCommand(VcPullAsync, () => VcSupported && SelectedWorkspace is not null);
    }

    private void RaiseVcCommands()
    {
        foreach (var command in new[] { VcCreate, VcMap, VcStatus, VcPush, VcPull })
        {
            command.RaiseCanExecuteChanged();
        }
    }

    // ---- bindable state ----------------------------------------------------

    public ObservableCollection<DeviceInfo> Devices { get; } = [];
    public ObservableCollection<BlockRow> Blocks { get; }
    public ICollectionView BlocksView { get; }

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];
    public ObservableCollection<MappedObjectInfo> VcStatusItems { get; } = [];

    public AsyncCommand RunDoctor { get; }
    public AsyncCommand Connect { get; }
    public AsyncCommand OpenProject { get; }
    public AsyncCommand Refresh { get; }
    public AsyncCommand Export { get; }
    public AsyncCommand Import { get; }
    public AsyncCommand Compile { get; }
    public AsyncCommand Inspect { get; }
    public AsyncCommand Save { get; }

    public AsyncCommand VcRefresh { get; }
    public AsyncCommand VcCreate { get; }
    public AsyncCommand VcMap { get; }
    public AsyncCommand VcStatus { get; }
    public AsyncCommand VcPush { get; }
    public AsyncCommand VcPull { get; }

    public string ProjectPath
    {
        get => _projectPath;
        set { if (Set(ref _projectPath, value)) OpenProject.RaiseCanExecuteChanged(); }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set { if (Set(ref _outputDirectory, value)) Export.RaiseCanExecuteChanged(); }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public string NamePattern { get => _namePattern; set => Set(ref _namePattern, value); }
    public bool UseMock { get => _useMock; set => Set(ref _useMock, value); }
    public bool Headless { get => _headless; set => Set(ref _headless, value); }
    public bool SourceFormat { get => _sourceFormat; set => Set(ref _sourceFormat, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public int ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    public string BlockFilter
    {
        get => _blockFilter;
        set { if (Set(ref _blockFilter, value)) BlocksView.Refresh(); }
    }

    /// <summary>False on TIA Portal below V21, which has no Version Control Interface.</summary>
    public bool VcSupported
    {
        get => _vcSupported;
        private set { if (Set(ref _vcSupported, value)) RaiseVcCommands(); }
    }

    public WorkspaceInfo? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set { if (Set(ref _selectedWorkspace, value)) RaiseVcCommands(); }
    }

    public string NewWorkspaceName
    {
        get => _newWorkspaceName;
        set { if (Set(ref _newWorkspaceName, value)) RaiseVcCommands(); }
    }

    public string NewWorkspaceFolder
    {
        get => _newWorkspaceFolder;
        set { if (Set(ref _newWorkspaceFolder, value)) RaiseVcCommands(); }
    }

    /// <summary>
    /// On by default. Mapping writes into the project and a pull overwrites blocks, so the
    /// destructive step is always one deliberate click away rather than the default.
    /// </summary>
    public bool VcDryRun { get => _vcDryRun; set => Set(ref _vcDryRun, value); }

    public bool VcShowAll { get => _vcShowAll; set => Set(ref _vcShowAll, value); }

    public DeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            foreach (var command in new[] { Refresh, Export, Import, Compile, Inspect, Save })
            {
                command.RaiseCanExecuteChanged();
            }
            if (value is not null) _ = RefreshBlocksAsync();
        }
    }

    public string SelectionSummary
    {
        get
        {
            var selected = Blocks.Count(b => b.Selected);
            return selected == 0
                ? $"{Blocks.Count} block(s); none selected - export will take all of them"
                : $"{selected} of {Blocks.Count} block(s) selected";
        }
    }

    // ---- commands ----------------------------------------------------------

    private async Task DoctorAsync() => await Guarded("Checking environment", async () =>
    {
        EnsureBridge();
        var report = await _client.DoctorAsync();

        Append("--- environment ---");
        Append($"{report.MachineName} / {report.UserName} / {(report.Is64BitProcess ? "x64" : "x86")}");
        foreach (var check in report.Checks)
        {
            Append($"[{check.Status}] {check.Title}: {check.Detail}");
            if (!string.IsNullOrWhiteSpace(check.Remedy)) Append($"        -> {check.Remedy}");
        }
        foreach (var install in report.Installations)
        {
            Append($"installed: V{install.Version} {install.EngineeringDllPath}");
        }

        Status = report.CanRunOpenness
            ? "Environment OK."
            : "Environment not ready - see the log.";
    });

    private async Task ConnectAsync() => await Guarded("Connecting to TIA Portal", async () =>
    {
        EnsureBridge();
        var state = await _client.ConnectAsync(!Headless);
        Status = $"Connected ({state.Mode}, Openness {state.OpennessVersion}).";

        // Attaching to a running TIA inherits whatever project it already had open.
        if (state.OpenProject is not null)
        {
            ProjectPath = state.OpenProject.Path ?? string.Empty;
            Append($"attached to an open project: {state.OpenProject.Name}");
            await LoadDevicesAsync();
        }
    });

    private async Task OpenProjectAsync() => await Guarded("Opening project", async () =>
    {
        EnsureBridge();
        var project = await _client.OpenProjectAsync(ProjectPath);
        Append($"opened {project.Name} ({project.Path})");
        Status = $"Project {project.Name} open.";
        await LoadDevicesAsync();
    });

    private async Task LoadDevicesAsync()
    {
        var devices = await _client.ListDevicesAsync();
        Devices.Clear();
        foreach (var device in devices) Devices.Add(device);

        SelectedDevice = Devices.FirstOrDefault(d => d.Category == "Plc") ?? Devices.FirstOrDefault();
        Append($"{devices.Count} device(s).");

        // Populate the version-control tab up front, so the feature is visibly absent on
        // TIA Portal below V21 rather than failing when someone clicks a button.
        await VcRefreshAsync();
    }

    private async Task RefreshBlocksAsync()
    {
        if (SelectedDevice is null) return;

        await Guarded($"Reading blocks of {SelectedDevice.Id}", async () =>
        {
            var blocks = await _client.ListBlocksAsync(SelectedDevice.Id);
            Blocks.Clear();
            foreach (var block in blocks)
            {
                var row = new BlockRow(block);
                row.PropertyChanged += OnRowChanged;
                Blocks.Add(row);
            }
            Raise(nameof(SelectionSummary));
            Status = $"{blocks.Count} block(s) in {SelectedDevice.Id}.";
        });
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlockRow.Selected)) Raise(nameof(SelectionSummary));
    }

    private async Task ExportAsync() => await Guarded("Exporting", async () =>
    {
        var selected = Blocks.Where(b => b.Selected).Select(b => b.Path).ToList();
        var format = SourceFormat ? ExportFormat.Source : ExportFormat.SimaticMl;

        var result = await _client.ExportBlocksAsync(SelectedDevice!.Id, selected, OutputDirectory, format);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            Append($"FAILED {item.BlockPath}: {item.Error}");
        }

        Status = $"Exported {result.Succeeded}/{result.Requested} to {result.OutputDirectory}" +
                 (result.Failed > 0 ? $" ({result.Failed} failed)" : string.Empty);
        Append(Status);
    });

    private async Task ImportAsync() => await Guarded("Importing", async () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select blocks to import",
            Filter = "Importable files|*.xml;*.scl;*.db;*.udt|SimaticML (*.xml)|*.xml|Sources|*.scl;*.db;*.udt|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var overwrite = MessageBox.Show(
            "Replace blocks that already exist in the project?\n\n" +
            "No  = fail on blocks that already exist (safer)\n" +
            "Yes = overwrite them",
            "Import", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (overwrite == MessageBoxResult.Cancel) return;

        var result = await _client.ImportBlocksAsync(
            SelectedDevice!.Id, dialog.FileNames, overwrite == MessageBoxResult.Yes);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            Append($"FAILED {item.FilePath}: {item.Error}");
        }

        Status = $"Imported {result.Succeeded}/{result.Requested} file(s). Not saved yet.";
        Append(Status);
        await RefreshBlocksAsync();
    });

    private async Task CompileAsync() => await Guarded("Compiling", async () =>
    {
        var result = await _client.CompileAsync(SelectedDevice!.Id);

        foreach (var message in Flatten(result.Messages).Where(m => m.Severity != CompileSeverity.Information))
        {
            Append($"{message.Severity}: {message.Target} - {message.Description}");
        }

        Status = $"{result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s) " +
                 $"in {result.Duration.TotalSeconds:F1}s";
        Append(Status);
        await RefreshBlocksAsync();
    });

    private async Task InspectAsync() => await Guarded("Inspecting", async () =>
    {
        var report = await _client.InspectAsync(SelectedDevice!.Id,
            string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern);

        Append($"--- inspection of {report.DeviceId} ---");
        foreach (var group in report.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Append($"{group.Key} ({group.Count()})");
            foreach (var finding in group) Append($"    [{finding.Severity}] {finding.Target}: {finding.Message}");
        }

        Status = $"{report.Findings.Count} finding(s) over {report.BlocksScanned} block(s).";
        Append(Status);
    });

    private async Task SaveAsync() => await Guarded("Saving project", async () =>
    {
        if (MessageBox.Show("Save the project to disk? This cannot be undone.", "Save",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await _client.SaveProjectAsync();
        Status = "Project saved.";
        Append(Status);
    });

    // ---- plumbing ----------------------------------------------------------

    /// <summary>
    /// Starts the bridge on first use rather than at construction, so the window opens even
    /// when the bridge is missing and the error lands in the log instead of a startup crash.
    /// </summary>
    private void EnsureBridge()
    {
        if (_started && _client.Bridge.IsRunning) return;

        _client.Start(forceMock: UseMock);
        _started = true;
        Append($"bridge started ({(UseMock ? "mock" : "openness")}).");
    }

    private async Task Guarded(string what, Func<Task> action)
    {
        Busy = true;
        Status = what + "...";
        try
        {
            await action();
        }
        catch (BridgeRpcException ex)
        {
            Status = ex.Message;
            Append("error: " + ex.Message);
            if (ex.Data2 is not null) Append(ex.Data2);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Append("error: " + ex.Message);
        }
        finally
        {
            Busy = false;
            ProgressMax = 0;
        }
    }

    private void OnProgress(TiaOpenness.Contracts.Rpc.ProgressPayload payload)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ProgressMax = payload.Total;
            ProgressValue = payload.Current;
            Status = $"{payload.Operation} {payload.Current}/{payload.Total}: {payload.Message}";
        });
    }

    private void Append(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}{System.Environment.NewLine}";

        // Bridge log lines arrive on a background reader thread.
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.Invoke(() => Log += stamped);
            return;
        }
        Log += stamped;
    }

    private bool FilterBlock(object item)
    {
        if (BlockFilter.Length == 0) return true;
        return item is BlockRow row
               && row.Path.IndexOf(BlockFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<CompileMessage> Flatten(IEnumerable<CompileMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            if (message.Children is null) continue;
            foreach (var child in Flatten(message.Children)) yield return child;
        }
    }

    public void SelectAll(bool selected)
    {
        foreach (var row in BlocksView.Cast<BlockRow>()) row.Selected = selected;
    }

    public void BrowseProject()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a TIA Portal project",
            Filter = "TIA Portal projects|*.ap21;*.ap20;*.ap19;*.ap18;*.ap17;*.ap16;*.ap15_1|All files|*.*",
        };
        if (dialog.ShowDialog() == true) ProjectPath = dialog.FileName;
    }

    public void BrowseWorkspaceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the workspace folder (your Git working tree)" };
        if (dialog.ShowDialog() == true) NewWorkspaceFolder = dialog.FolderName;
    }

    public void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the export folder" };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    // ---- version control ---------------------------------------------------

    /// <summary>
    /// Loads the VCI panel. Absent below TIA Portal V21, in which case the panel disables itself
    /// rather than offering buttons that can only fail.
    /// </summary>
    private async Task VcRefreshAsync() => await Guarded("Reading version control", async () =>
    {
        EnsureBridge();
        VcSupported = await _client.VcSupportedAsync();

        Workspaces.Clear();
        VcStatusItems.Clear();

        if (!VcSupported)
        {
            Status = "This project has no Version Control Interface (needs TIA Portal V21+).";
            Append(Status);
            return;
        }

        foreach (var workspace in await _client.VcListWorkspacesAsync()) Workspaces.Add(workspace);
        SelectedWorkspace ??= Workspaces.FirstOrDefault();

        if (Workspaces.Count == 0)
        {
            Status = "No workspace yet. Point one at your Git working tree and create it.";
            return;
        }

        Status = $"{Workspaces.Count} workspace(s).";

        // Load the diff straight away: opening the tab should answer "what changed?" without
        // a second click, and it is read-only.
        await LoadVcStatusAsync();
    });

    private async Task LoadVcStatusAsync()
    {
        var report = await _client.VcStatusAsync(SelectedWorkspace?.Name, changedOnly: !VcShowAll);

        VcStatusItems.Clear();
        foreach (var item in report.Items) VcStatusItems.Add(item);

        Status = report.InSync
            ? $"{report.Total} mapped object(s), all in sync - nothing to commit."
            : $"{report.Total} mapped object(s), {report.Differing} differ.";
    }

    private async Task VcCreateAsync() => await Guarded("Creating workspace", async () =>
    {
        var workspace = await _client.VcCreateWorkspaceAsync(NewWorkspaceName, NewWorkspaceFolder);
        Append($"created workspace '{workspace.Name}' at {workspace.RootPath}");
        await VcRefreshAsync();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == workspace.Name);
        Status = "Workspace created. Now map the project into it.";
    });

    private async Task VcMapAsync() => await Guarded("Mapping project", async () =>
    {
        var result = await _client.VcMapProjectAsync(SelectedWorkspace?.Name, SelectedDevice?.Id, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Outcome is "failed" or "unsupported"))
        {
            Append($"{item.Outcome}: {item.Target} - {item.Error}");
        }

        Status = result.DryRun
            ? $"Dry run: {result.Mapped} would be mapped, {result.AlreadyMapped} already, " +
              $"{result.Unsupported} unsupported. Clear 'Dry run' to apply."
            : $"Mapped {result.Mapped}, already {result.AlreadyMapped}, unsupported {result.Unsupported}, " +
              $"failed {result.Failed}.";

        Append(Status);
        if (!result.DryRun) await VcStatusAsync();
    });

    private async Task VcStatusAsync() => await Guarded("Comparing with workspace", async () =>
    {
        await LoadVcStatusAsync();
        Append(Status);
    });

    private async Task VcPushAsync() => await VcSyncAsync(SyncDirection.ProjectToWorkspace);

    private async Task VcPullAsync()
    {
        if (!VcDryRun && MessageBox.Show(
                "Read the workspace's text files back INTO the project?\n\n" +
                "This OVERWRITES blocks in the open project and cannot be undone.\n" +
                "Compile and save afterwards.",
                "Restore from workspace", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        await VcSyncAsync(SyncDirection.WorkspaceToProject);
    }

    private async Task VcSyncAsync(SyncDirection direction) => await Guarded($"Synchronizing {direction}", async () =>
    {
        var result = await _client.VcSyncAsync(SelectedWorkspace?.Name, direction, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Error is not null))
        {
            Append($"FAILED {item.Name}: {item.Error}");
        }

        Status = result.DryRun
            ? $"Dry run: {result.Synchronized} would sync {direction}, {result.SkippedEqual} already equal. " +
              "Clear 'Dry run' to apply."
            : $"{result.Synchronized} synchronized, {result.Failed} failed, {result.SkippedEqual} already equal.";

        Append(Status);

        if (!result.DryRun)
        {
            Append(direction == SyncDirection.ProjectToWorkspace
                ? $"Commit from {result.RootPath}:  git add -A && git commit"
                : "The project now holds the workspace's version - compile and save it.");
            await VcStatusAsync();
        }
    });

    /// <summary>
    /// Applies command-line startup options: <c>--mock</c> to use the synthetic backend and
    /// <c>--project &lt;path&gt;</c> to open one immediately. Lets the app be demonstrated, and
    /// screenshotted, without a TIA Portal installation.
    /// </summary>
    public async Task ApplyStartupAsync(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--mock", StringComparison.OrdinalIgnoreCase)))
        {
            UseMock = true;
        }

        var index = Array.FindIndex(args, a => string.Equals(a, "--project", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length) ProjectPath = args[index + 1];
        else if (UseMock) ProjectPath = @"D:\demo\Line.ap21";

        if (!UseMock) return;

        await ConnectAsync();
        if (ProjectPath.Length > 0) await OpenProjectAsync();
    }

    public void Dispose() => _client.Dispose();
}
