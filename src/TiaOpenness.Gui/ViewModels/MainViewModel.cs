using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;
using TiaOpenness.Gui.Localization;

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
        Info.IsKnowHowProtected ? Loc.Current["Block.Protected"]
        : !Info.IsConsistent ? Loc.Current["Block.NeedsCompiling"]
        : string.Empty;

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Called when the language changes; <see cref="Status"/> is translated on read.</summary>
    public void RefreshLocalizedText() => Raise(nameof(Status));
}

/// <summary>Drives the single main window.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly TiaClient _client = new();
    private bool _started;

    private string _projectPath = string.Empty;
    private string _projectName = string.Empty;
    private string _outputDirectory = string.Empty;
    private LocalizedText _status = LocalizedText.Key("Status.NotConnected");
    private string _log = string.Empty;
    private string _blockFilter = string.Empty;
    private string _namePattern = "^(OB|FB|FC|DB|UDT)_";
    private DeviceInfo? _selectedDevice;
    private bool _useMock;
    private bool _headless;
    private bool _sourceFormat;
    private bool _busy;
    private bool _isConnected;
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

        Devices.CollectionChanged += OnDevicesChanged;

        // Anything shown as text but stored as a key has to be re-read when the language flips.
        Loc.Current.LanguageChanged += OnLanguageChanged;

        _client.Bridge.Log += (_, e) => Append("bridge: " + e.Line);
        _client.Bridge.Progress += (_, e) => OnProgress(e.Progress);
        _client.Bridge.Exited += (_, _) =>
        {
            IsConnected = false;
            Append(Loc.Current["Log.BridgeExited"]);
        };

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

    /// <summary>
    /// Re-reads everything whose text is a catalogue key rather than a literal. The log is left
    /// alone on purpose: it is a record of what was reported at the time.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Raise(nameof(Status));
        Raise(nameof(SelectionSummary));
        Raise(nameof(WorkspaceRootDisplay));
        foreach (var row in Blocks) row.RefreshLocalizedText();
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Raise(nameof(HasNoDevices));

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

    /// <summary>Shown beside the app name in the title bar, the way macOS names the document.</summary>
    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set { if (Set(ref _outputDirectory, value)) Export.RaiseCanExecuteChanged(); }
    }

    public string Status => _status.Resolve();

    public string Log { get => _log; private set => Set(ref _log, value); }
    public string NamePattern { get => _namePattern; set => Set(ref _namePattern, value); }
    public bool UseMock { get => _useMock; set => Set(ref _useMock, value); }
    public bool Headless { get => _headless; set => Set(ref _headless, value); }
    public bool SourceFormat { get => _sourceFormat; set => Set(ref _sourceFormat, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }
    public int ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    public bool HasNoDevices => Devices.Count == 0;

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
        set
        {
            if (!Set(ref _selectedWorkspace, value)) return;
            Raise(nameof(WorkspaceRootDisplay));
            RaiseVcCommands();
        }
    }

    /// <summary>The workspace's folder, or the sentence that explains there is not one yet.</summary>
    public string WorkspaceRootDisplay
        => SelectedWorkspace?.RootPath ?? Loc.Current["Vc.NoWorkspace"];

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
                ? Loc.Current.T("Blocks.Summary.None", Blocks.Count)
                : Loc.Current.T("Blocks.Summary.Some", selected, Blocks.Count);
        }
    }

    // ---- commands ----------------------------------------------------------

    private async Task DoctorAsync() => await Guarded("Status.CheckingEnvironment", async () =>
    {
        EnsureBridge();
        var report = await _client.DoctorAsync();

        Append(Loc.Current["Log.EnvironmentHeader"]);
        Append($"{report.MachineName} / {report.UserName} / {(report.Is64BitProcess ? "x64" : "x86")}");
        foreach (var check in report.Checks)
        {
            Append($"[{check.Status}] {check.Title}: {check.Detail}");
            if (!string.IsNullOrWhiteSpace(check.Remedy)) Append($"        -> {check.Remedy}");
        }
        foreach (var install in report.Installations)
        {
            Append(Loc.Current.T("Log.Installed", install.Version, install.EngineeringDllPath));
        }

        if (report.CanRunOpenness) SetStatus("Status.EnvOk");
        else SetStatus("Status.EnvNotReady");
    });

    private async Task ConnectAsync() => await Guarded("Status.Connecting", async () =>
    {
        EnsureBridge();
        var state = await _client.ConnectAsync(!Headless);
        IsConnected = true;
        SetStatus("Status.Connected", state.Mode, state.OpennessVersion);

        // Attaching to a running TIA inherits whatever project it already had open.
        if (state.OpenProject is not null)
        {
            ProjectPath = state.OpenProject.Path ?? string.Empty;
            ProjectName = state.OpenProject.Name ?? string.Empty;
            Append(Loc.Current.T("Log.Attached", state.OpenProject.Name));
            await LoadDevicesAsync();
        }
    });

    private async Task OpenProjectAsync() => await Guarded("Status.OpeningProject", async () =>
    {
        EnsureBridge();
        var project = await _client.OpenProjectAsync(ProjectPath);
        ProjectName = project.Name ?? string.Empty;
        Append(Loc.Current.T("Log.Opened", project.Name, project.Path));
        SetStatus("Status.ProjectOpen", project.Name);
        await LoadDevicesAsync();
    });

    private async Task LoadDevicesAsync()
    {
        var devices = await _client.ListDevicesAsync();
        Devices.Clear();
        foreach (var device in devices) Devices.Add(device);

        SelectedDevice = Devices.FirstOrDefault(d => d.Category == "Plc") ?? Devices.FirstOrDefault();
        Append(Loc.Current.T("Log.DeviceCount", devices.Count));

        // Populate the version-control tab up front, so the feature is visibly absent on
        // TIA Portal below V21 rather than failing when someone clicks a button.
        await VcRefreshAsync();
    }

    private async Task RefreshBlocksAsync()
    {
        if (SelectedDevice is null) return;

        var deviceId = SelectedDevice.Id;
        await Guarded("Status.ReadingBlocks", async () =>
        {
            var blocks = await _client.ListBlocksAsync(deviceId);
            Blocks.Clear();
            foreach (var block in blocks)
            {
                var row = new BlockRow(block);
                row.PropertyChanged += OnRowChanged;
                Blocks.Add(row);
            }
            Raise(nameof(SelectionSummary));
            SetStatus("Status.BlocksIn", blocks.Count, deviceId);
        }, deviceId);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlockRow.Selected)) Raise(nameof(SelectionSummary));
    }

    private async Task ExportAsync() => await Guarded("Status.Exporting", async () =>
    {
        var selected = Blocks.Where(b => b.Selected).Select(b => b.Path).ToList();
        var format = SourceFormat ? ExportFormat.Source : ExportFormat.SimaticMl;

        var result = await _client.ExportBlocksAsync(SelectedDevice!.Id, selected, OutputDirectory, format);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            Append(Loc.Current.T("Log.Failed", item.BlockPath, item.Error));
        }

        if (result.Failed > 0)
        {
            SetStatus("Status.ExportedFailed",
                result.Succeeded, result.Requested, result.OutputDirectory, result.Failed);
        }
        else
        {
            SetStatus("Status.Exported", result.Succeeded, result.Requested, result.OutputDirectory);
        }

        Append(Status);
    });

    private async Task ImportAsync() => await Guarded("Status.Importing", async () =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Current["Dialog.Import.Title"],
            Filter = Loc.Current["Dialog.Import.Filter"],
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var overwrite = MessageBox.Show(
            Loc.Current["Dialog.Import.Text"],
            Loc.Current["Dialog.Import.Caption"], MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (overwrite == MessageBoxResult.Cancel) return;

        var result = await _client.ImportBlocksAsync(
            SelectedDevice!.Id, dialog.FileNames, overwrite == MessageBoxResult.Yes);

        foreach (var item in result.Items.Where(i => !i.Succeeded))
        {
            Append(Loc.Current.T("Log.Failed", item.FilePath, item.Error));
        }

        SetStatus("Status.Imported", result.Succeeded, result.Requested);
        Append(Status);
        await RefreshBlocksAsync();
    });

    private async Task CompileAsync() => await Guarded("Status.Compiling", async () =>
    {
        var result = await _client.CompileAsync(SelectedDevice!.Id);

        foreach (var message in Flatten(result.Messages).Where(m => m.Severity != CompileSeverity.Information))
        {
            Append($"{message.Severity}: {message.Target} - {message.Description}");
        }

        SetStatus("Status.CompileResult",
            result.State, result.ErrorCount, result.WarningCount,
            result.Duration.TotalSeconds.ToString("F1", CultureInfo.CurrentCulture));

        Append(Status);
        await RefreshBlocksAsync();
    });

    private async Task InspectAsync() => await Guarded("Status.Inspecting", async () =>
    {
        var report = await _client.InspectAsync(SelectedDevice!.Id,
            string.IsNullOrWhiteSpace(NamePattern) ? null : NamePattern);

        Append(Loc.Current.T("Log.InspectionHeader", report.DeviceId));
        foreach (var group in report.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Append($"{group.Key} ({group.Count()})");
            foreach (var finding in group) Append($"    [{finding.Severity}] {finding.Target}: {finding.Message}");
        }

        SetStatus("Status.InspectResult", report.Findings.Count, report.BlocksScanned);
        Append(Status);
    });

    private async Task SaveAsync() => await Guarded("Status.SavingProject", async () =>
    {
        if (MessageBox.Show(
                Loc.Current["Dialog.Save.Text"], Loc.Current["Dialog.Save.Caption"],
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        await _client.SaveProjectAsync();
        SetStatus("Status.ProjectSaved");
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
        Append(Loc.Current.T("Log.BridgeStarted", UseMock ? "mock" : "openness"));
    }

    private void SetStatus(string key, params object?[] args)
    {
        _status = LocalizedText.Key(key, args);
        Raise(nameof(Status));
    }

    /// <summary>For text that is already final - an exception message from the bridge.</summary>
    private void SetStatusLiteral(string text)
    {
        _status = LocalizedText.Literal(text);
        Raise(nameof(Status));
    }

    private async Task Guarded(string workingKey, Func<Task> action, params object?[] workingArgs)
    {
        Busy = true;
        _status = LocalizedText.Working(workingKey, workingArgs);
        Raise(nameof(Status));

        try
        {
            await action();
        }
        catch (BridgeRpcException ex)
        {
            SetStatusLiteral(ex.Message);
            Append(Loc.Current.T("Log.Error", ex.Message));
            if (ex.Data2 is not null) Append(ex.Data2);
        }
        catch (Exception ex)
        {
            SetStatusLiteral(ex.Message);
            Append(Loc.Current.T("Log.Error", ex.Message));
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
            SetStatusLiteral($"{payload.Operation} {payload.Current}/{payload.Total}: {payload.Message}");
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

    public void ClearLog() => Log = string.Empty;

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
            Title = Loc.Current["Dialog.OpenProject.Title"],
            Filter = Loc.Current["Dialog.OpenProject.Filter"],
        };
        if (dialog.ShowDialog() == true) ProjectPath = dialog.FileName;
    }

    public void BrowseWorkspaceFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Current["Dialog.Workspace.Title"] };
        if (dialog.ShowDialog() == true) NewWorkspaceFolder = dialog.FolderName;
    }

    public void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Current["Dialog.Export.Title"] };
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    // ---- version control ---------------------------------------------------

    /// <summary>
    /// Loads the VCI panel. Absent below TIA Portal V21, in which case the panel disables itself
    /// rather than offering buttons that can only fail.
    /// </summary>
    private async Task VcRefreshAsync() => await Guarded("Status.ReadingVc", async () =>
    {
        EnsureBridge();
        VcSupported = await _client.VcSupportedAsync();

        Workspaces.Clear();
        VcStatusItems.Clear();

        if (!VcSupported)
        {
            SetStatus("Status.VcUnsupported");
            Append(Status);
            return;
        }

        foreach (var workspace in await _client.VcListWorkspacesAsync()) Workspaces.Add(workspace);
        SelectedWorkspace ??= Workspaces.FirstOrDefault();

        if (Workspaces.Count == 0)
        {
            SetStatus("Status.VcNoWorkspace");
            return;
        }

        SetStatus("Status.VcWorkspaces", Workspaces.Count);

        // Load the diff straight away: opening the tab should answer "what changed?" without
        // a second click, and it is read-only.
        await LoadVcStatusAsync();
    });

    private async Task LoadVcStatusAsync()
    {
        var report = await _client.VcStatusAsync(SelectedWorkspace?.Name, changedOnly: !VcShowAll);

        VcStatusItems.Clear();
        foreach (var item in report.Items) VcStatusItems.Add(item);

        if (report.InSync) SetStatus("Status.VcInSync", report.Total);
        else SetStatus("Status.VcDiffer", report.Total, report.Differing);
    }

    private async Task VcCreateAsync() => await Guarded("Status.CreatingWorkspace", async () =>
    {
        var workspace = await _client.VcCreateWorkspaceAsync(NewWorkspaceName, NewWorkspaceFolder);
        Append(Loc.Current.T("Log.WorkspaceCreated", workspace.Name, workspace.RootPath));
        await VcRefreshAsync();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Name == workspace.Name);
        SetStatus("Status.VcCreated");
    });

    private async Task VcMapAsync() => await Guarded("Status.MappingProject", async () =>
    {
        var result = await _client.VcMapProjectAsync(SelectedWorkspace?.Name, SelectedDevice?.Id, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Outcome is "failed" or "unsupported"))
        {
            Append($"{item.Outcome}: {item.Target} - {item.Error}");
        }

        if (result.DryRun)
        {
            SetStatus("Status.VcMapDry", result.Mapped, result.AlreadyMapped, result.Unsupported);
        }
        else
        {
            SetStatus("Status.VcMapApplied",
                result.Mapped, result.AlreadyMapped, result.Unsupported, result.Failed);
        }

        Append(Status);
        if (!result.DryRun) await VcStatusAsync();
    });

    private async Task VcStatusAsync() => await Guarded("Status.ComparingWorkspace", async () =>
    {
        await LoadVcStatusAsync();
        Append(Status);
    });

    private async Task VcPushAsync() => await VcSyncAsync(SyncDirection.ProjectToWorkspace);

    private async Task VcPullAsync()
    {
        if (!VcDryRun && MessageBox.Show(
                Loc.Current["Dialog.Pull.Text"], Loc.Current["Dialog.Pull.Caption"],
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        await VcSyncAsync(SyncDirection.WorkspaceToProject);
    }

    private async Task VcSyncAsync(SyncDirection direction)
        => await Guarded("Status.Synchronizing", async () =>
    {
        var result = await _client.VcSyncAsync(SelectedWorkspace?.Name, direction, VcDryRun);

        foreach (var item in result.Items.Where(i => i.Error is not null))
        {
            Append(Loc.Current.T("Log.Failed", item.Name, item.Error));
        }

        if (result.DryRun)
        {
            SetStatus("Status.VcSyncDry", result.Synchronized, direction, result.SkippedEqual);
        }
        else
        {
            SetStatus("Status.VcSyncApplied", result.Synchronized, result.Failed, result.SkippedEqual);
        }

        Append(Status);

        if (!result.DryRun)
        {
            Append(direction == SyncDirection.ProjectToWorkspace
                ? Loc.Current.T("Log.CommitHint", result.RootPath)
                : Loc.Current["Log.PullHint"]);
            await VcStatusAsync();
        }
    }, direction);

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

    public void Dispose()
    {
        Loc.Current.LanguageChanged -= OnLanguageChanged;
        _client.Dispose();
    }
}
