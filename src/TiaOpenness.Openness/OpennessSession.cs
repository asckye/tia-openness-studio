using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Abstractions;
using TiaOpenness.Core.Inspection;

namespace TiaOpenness.Openness
{
    /// <summary>
    /// The real <see cref="ITiaSession"/>, backed by Siemens.Engineering.
    ///
    /// Lives only inside the bridge process, which is bound to exactly one Openness version
    /// by <c>OpennessAssemblyResolver</c> before this type is ever touched.
    /// </summary>
    internal sealed class OpennessSession : ITiaSession
    {
        private readonly string _boundVersion;
        private TiaPortal _portal;
        private Project _project;
        private bool _withUi;
        private OpennessVersionControl _versionControl;
        private Project _versionControlOwner;

        /// <summary>Device name -> indexed software. Rebuilt whenever the project changes.</summary>
        private readonly Dictionary<string, PlcContext> _plcs =
            new Dictionary<string, PlcContext>(StringComparer.OrdinalIgnoreCase);

        public OpennessSession(string boundVersion)
        {
            _boundVersion = boundVersion;
        }

        public SessionMode Mode { get { return SessionMode.Openness; } }
        public bool IsConnected { get { return _portal != null; } }
        public bool HasProject { get { return _project != null; } }

        /// <summary>
        /// The project's Version Control Interface, or null on TIA Portal below V21 where the
        /// service does not exist. Built once per project: the VCI object graph is COM-backed and
        /// dies if the service it came from is collected, so it must not be re-created per call.
        /// </summary>
        public IVersionControl VersionControl
        {
            get
            {
                if (_project == null) return null;

                if (_versionControl == null || !ReferenceEquals(_versionControlOwner, _project))
                {
                    if (!OpennessVersionControl.IsAvailable(_project)) return null;

                    _versionControl = new OpennessVersionControl(() => _project);
                    _versionControlOwner = _project;
                }
                return _versionControl;
            }
        }

        // ---- session -------------------------------------------------------

        public SessionState Connect(bool withUserInterface, bool attachToRunning, string version)
        {
            if (_portal != null) return GetState();

            _withUi = withUserInterface;

            if (attachToRunning)
            {
                _portal = TryAttach();
                if (_portal != null)
                {
                    // An attached instance keeps whatever project it already had open.
                    _project = _portal.Projects.SafeEnumerate().FirstOrDefault();
                    if (_project != null) IndexProject();
                    return GetState();
                }
            }

            _portal = new TiaPortal(withUserInterface
                ? TiaPortalMode.WithUserInterface
                : TiaPortalMode.WithoutUserInterface);

            return GetState();
        }

        /// <summary>
        /// Attaches to a running TIA Portal. Each attach can fail on its own (the instance may
        /// be shutting down, or owned by another user), so failures are skipped rather than fatal.
        /// </summary>
        private static TiaPortal TryAttach()
        {
            IList<TiaPortalProcess> processes;
            try { processes = TiaPortal.GetProcesses(); }
            catch (Exception) { return null; }

            foreach (var process in processes)
            {
                try { return process.Attach(); }
                catch (Exception) { /* try the next instance */ }
            }
            return null;
        }

        public void Disconnect()
        {
            _plcs.Clear();
            _versionControl = null;
            _versionControlOwner = null;
            _project = null;

            if (_portal == null) return;
            try { _portal.Dispose(); }
            catch (Exception) { /* TIA may already be gone */ }
            _portal = null;
        }

        public SessionState GetState()
        {
            return new SessionState
            {
                Connected = _portal != null,
                Mode = SessionMode.Openness,
                OpennessVersion = _boundVersion,
                WithUserInterface = _withUi,
                OpenProject = _project == null ? null : Describe(_project),
            };
        }

        // ---- project -------------------------------------------------------

        public ProjectInfo OpenProject(string path)
        {
            RequireConnected();

            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException("Project file not found: " + path, path);
            }

            // Re-opening the same project would throw; reuse what is already open.
            var already = _portal.Projects.SafeEnumerate().FirstOrDefault(p =>
                string.Equals(p.Path?.FullName, file.FullName, StringComparison.OrdinalIgnoreCase));

            _project = already ?? _portal.Projects.Open(file);
            IndexProject();
            return Describe(_project);
        }

        public ProjectInfo GetProjectInfo()
        {
            RequireProject();
            return Describe(_project);
        }

        public void SaveProject()
        {
            RequireProject();
            _project.Save();
        }

        public void CloseProject()
        {
            if (_project == null) return;
            try { _project.Close(); }
            finally
            {
                _project = null;
                _versionControl = null;
                _versionControlOwner = null;
                _plcs.Clear();
            }
        }

        private static ProjectInfo Describe(Project project)
        {
            return new ProjectInfo
            {
                Name = project.Name,
                Path = project.Path?.FullName,
                Author = project.Prop<string>("Author"),
                Comment = (project.Prop<object>("Comment") as MultilingualText).AsText(),
                CreationTime = project.Prop<DateTime>("CreationTime").AsOffset(),
                LastModified = project.Prop<DateTime>("LastModified").AsOffset(),
                IsModified = project.Prop("IsModified", false),
            };
        }

        // ---- devices and blocks --------------------------------------------

        public IReadOnlyList<DeviceInfo> ListDevices()
        {
            RequireProject();
            return PlcNavigator.AllDevices(_project).Select(PlcNavigator.Describe).ToList();
        }

        public IReadOnlyList<BlockInfo> ListBlocks(string deviceId, bool includeSystemBlocks)
        {
            var plc = RequirePlc(deviceId);
            var blocks = new List<BlockInfo>();

            foreach (var pair in plc.Blocks)
            {
                var info = PlcNavigator.Describe(pair.Value, pair.Key);
                if (!includeSystemBlocks && IsSystemBlock(info)) continue;
                blocks.Add(info);
            }

            foreach (var pair in plc.Types)
            {
                blocks.Add(PlcNavigator.Describe(pair.Value, pair.Key));
            }

            return blocks.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Blocks TIA generates itself (safety runtime, motion control, library instances).
        /// They compile from the project's own configuration, so exporting them is noise.
        /// </summary>
        private static bool IsSystemBlock(BlockInfo block)
        {
            if (block.Name.StartsWith("_", StringComparison.Ordinal)) return true;
            if (block.Number.HasValue && block.Number.Value >= 60000) return true;
            return false;
        }

        // ---- export --------------------------------------------------------

        public ExportResult ExportBlocks(string deviceId, IReadOnlyList<string> blockPaths, string outputDirectory,
            ExportFormat format, bool preserveFolders, ProgressCallback progress)
        {
            var plc = RequirePlc(deviceId);
            var targets = ResolveExportTargets(plc, blockPaths);

            Directory.CreateDirectory(outputDirectory);
            var result = new ExportResult { OutputDirectory = outputDirectory, Requested = targets.Count };

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (progress != null) progress("export", i + 1, targets.Count, target.Path);

                var item = new ExportedItem { BlockPath = target.Path };
                try
                {
                    var folder = preserveFolders ? FolderOf(target.Path) : string.Empty;
                    var directory = string.IsNullOrEmpty(folder)
                        ? outputDirectory
                        : Path.Combine(outputDirectory, folder.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(directory);

                    item.FilePath = format == ExportFormat.SimaticMl
                        ? ExportAsXml(target, directory)
                        : ExportAsSource(plc, target, directory);

                    item.Succeeded = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = Explain(ex, target);
                    result.Failed++;
                }
                result.Items.Add(item);
            }

            return result;
        }

        private static string ExportAsXml(ExportTarget target, string directory)
        {
            var file = new FileInfo(Path.Combine(directory, SafeFileName(target.Name) + ".xml"));
            if (file.Exists) file.Delete();

            if (target.Block != null) target.Block.Export(file, ExportOptions.WithDefaults);
            else target.Type.Export(file, ExportOptions.WithDefaults);

            return file.FullName;
        }

        /// <summary>
        /// Text export goes through the external-source generator, which is the only route TIA
        /// offers to SCL/DB/UDT text. It only works for blocks written in a textual language.
        /// </summary>
        private static string ExportAsSource(PlcContext plc, ExportTarget target, string directory)
        {
            var extension = target.Type != null ? ".udt"
                : target.Block is DataBlock ? ".db"
                : ".scl";

            var file = new FileInfo(Path.Combine(directory, SafeFileName(target.Name) + extension));
            if (file.Exists) file.Delete();

            if (target.Block != null)
            {
                plc.Software.ExternalSourceGroup.GenerateSource(
                    new[] { target.Block }, file, GenerateOptions.None);
            }
            else
            {
                plc.Software.ExternalSourceGroup.GenerateSource(
                    new[] { target.Type }, file, GenerateOptions.None);
            }

            return file.FullName;
        }

        /// <summary>Turns the two failures engineers actually hit into instructions.</summary>
        private static string Explain(Exception ex, ExportTarget target)
        {
            if (target.KnowHowProtected)
            {
                return "Block is know-how protected and cannot be exported. Remove the protection in TIA Portal first. " +
                       "(" + ex.Message + ")";
            }
            if (!target.Consistent)
            {
                return "Block is inconsistent; TIA can only export a block that compiles. Compile the device first. " +
                       "(" + ex.Message + ")";
            }
            return ex.Message;
        }

        // ---- import --------------------------------------------------------

        public ExportResult ImportBlocks(string deviceId, IReadOnlyList<string> files, bool overwrite,
            ProgressCallback progress)
        {
            var plc = RequirePlc(deviceId);
            var result = new ExportResult { Requested = files.Count };

            for (var i = 0; i < files.Count; i++)
            {
                var path = files[i];
                if (progress != null) progress("import", i + 1, files.Count, path);

                var item = new ExportedItem { FilePath = path };
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("Import file not found.", path);

                    var extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
                    item.BlockPath = extension == ".xml"
                        ? ImportSimaticMl(plc, path, overwrite)
                        : ImportExternalSource(plc, path);

                    item.Succeeded = true;
                    result.Succeeded++;
                }
                catch (Exception ex)
                {
                    item.Succeeded = false;
                    item.Error = ex.Message;
                    result.Failed++;
                }
                result.Items.Add(item);
            }

            Index(plc);
            return result;
        }

        private static string ImportSimaticMl(PlcContext plc, string path, bool overwrite)
        {
            var file = new FileInfo(path);
            var options = overwrite ? ImportOptions.Override : ImportOptions.None;

            // A UDT and a block both arrive as .xml; the root element decides which composition
            // accepts it, and TIA gives no way to ask beforehand.
            if (LooksLikeType(path))
            {
                var types = plc.Software.TypeGroup.Types.Import(file, options);
                return string.Join(", ", types.Select(t => t.Name));
            }

            var blocks = plc.Software.BlockGroup.Blocks.Import(file, options);
            return string.Join(", ", blocks.Select(b => b.Name));
        }

        private static bool LooksLikeType(string path)
        {
            try
            {
                using (var reader = new StreamReader(path))
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;
                        if (line.IndexOf("SW.Types.PlcStruct", StringComparison.Ordinal) >= 0) return true;
                        if (line.IndexOf("SW.Blocks.", StringComparison.Ordinal) >= 0) return false;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the block path; a wrong guess surfaces as a normal import error.
            }
            return false;
        }

        /// <summary>
        /// SCL/DB/UDT text has to become an external source first, then be compiled into blocks.
        /// The temporary source object is removed afterwards so it does not clutter the project.
        /// </summary>
        private static string ImportExternalSource(PlcContext plc, string path)
        {
            var name = Path.GetFileName(path);
            PlcExternalSource source = null;
            try
            {
                source = plc.Software.ExternalSourceGroup.ExternalSources.Find(name);
                if (source != null) source.Delete();

                source = plc.Software.ExternalSourceGroup.ExternalSources.CreateFromFile(name, path);
                source.GenerateBlocksFromSource();
                return Path.GetFileNameWithoutExtension(path);
            }
            finally
            {
                try { source?.Delete(); }
                catch (Exception) { /* leaving it behind is harmless */ }
            }
        }

        // ---- tags ----------------------------------------------------------

        public IReadOnlyList<TagTableInfo> ListTagTables(string deviceId)
        {
            var plc = RequirePlc(deviceId);
            return plc.TagTables
                .Select(pair => PlcNavigator.Describe(pair.Value, pair.Key))
                .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<TagInfo> ListTags(string deviceId, string tableName)
        {
            var plc = RequirePlc(deviceId);

            IEnumerable<KeyValuePair<string, PlcTagTable>> tables = plc.TagTables;
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                tables = plc.TagTables.Where(p =>
                    string.Equals(p.Key, tableName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Value.Name, tableName, StringComparison.OrdinalIgnoreCase));

                if (!tables.Any())
                {
                    throw new KeyNotFoundException("No tag table named '" + tableName + "' on device '" + deviceId +
                        "'. Known tables: " + string.Join(", ", plc.TagTables.Keys));
                }
            }

            return tables
                .SelectMany(pair => pair.Value.Tags.SafeEnumerate()
                    .Select(tag => PlcNavigator.Describe(tag, pair.Value.Name)))
                .ToList();
        }

        // ---- compile -------------------------------------------------------

        public CompileResult CompileDevice(string deviceId, bool softwareOnly)
        {
            var plc = RequirePlc(deviceId);

            ICompilable compilable;
            if (softwareOnly)
            {
                compilable = plc.Software.GetService<ICompilable>();
            }
            else
            {
                var device = PlcNavigator.AllDevices(_project)
                    .FirstOrDefault(d => string.Equals(d.Name, plc.DeviceId, StringComparison.OrdinalIgnoreCase));
                var carrier = device == null
                    ? null
                    : PlcNavigator.AllDeviceItems(device).FirstOrDefault(i => i.GetService<ICompilable>() != null);
                compilable = carrier?.GetService<ICompilable>() ?? plc.Software.GetService<ICompilable>();
            }

            if (compilable == null)
            {
                throw new NotSupportedException("Device '" + deviceId + "' does not offer a compile service.");
            }

            var stopwatch = Stopwatch.StartNew();
            var compilerResult = compilable.Compile();
            stopwatch.Stop();

            Index(plc);

            return new CompileResult
            {
                State = compilerResult.State.ToString(),
                ErrorCount = compilerResult.ErrorCount,
                WarningCount = compilerResult.WarningCount,
                Duration = stopwatch.Elapsed,
                Messages = compilerResult.Messages.SafeEnumerate().Select(Describe).ToList(),
            };
        }

        private static CompileMessage Describe(CompilerResultMessage message)
        {
            return new CompileMessage
            {
                Severity = SeverityOf(message.State.ToString()),
                Description = message.Description,
                Target = message.Path,
                ErrorCode = message.Prop<string>("ErrorCode"),
                Children = message.Messages.SafeEnumerate().Select(Describe).ToList(),
            };
        }

        private static CompileSeverity SeverityOf(string state)
        {
            if (string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase)) return CompileSeverity.Error;
            if (string.Equals(state, "Warning", StringComparison.OrdinalIgnoreCase)) return CompileSeverity.Warning;
            return CompileSeverity.Information;
        }

        // ---- inspection ----------------------------------------------------

        public InspectionReport Inspect(string deviceId, InspectionOptions options)
        {
            var plc = RequirePlc(deviceId);
            var blocks = ListBlocks(deviceId, includeSystemBlocks: false);

            ISet<string> referenced = null;
            if (options != null && options.FindUnusedBlocks)
            {
                referenced = BuildReferenceIndex(plc, blocks);
            }

            return InspectionEngine.Run(plc.DeviceId, blocks, options, referenced);
        }

        /// <summary>
        /// Openness exposes no call graph, so references are read out of a throwaway SimaticML
        /// export. It costs one export of the whole program, which is why the dead-code rule is
        /// opt-in rather than always on.
        /// </summary>
        private ISet<string> BuildReferenceIndex(PlcContext plc, IReadOnlyList<BlockInfo> blocks)
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var temp = Path.Combine(Path.GetTempPath(), "tia-openness-refs-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(temp);
                var names = new HashSet<string>(blocks.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

                foreach (var block in blocks.Where(b => b.IsConsistent && !b.IsKnowHowProtected))
                {
                    PlcBlock plcBlock;
                    if (!plc.Blocks.TryGetValue(block.Path, out plcBlock)) continue;

                    var file = new FileInfo(Path.Combine(temp, SafeFileName(block.Name) + ".xml"));
                    try { plcBlock.Export(file, ExportOptions.None); }
                    catch (Exception) { continue; }

                    var xml = File.ReadAllText(file.FullName);
                    foreach (Match match in Regex.Matches(xml, "Name=\"([^\"]+)\""))
                    {
                        var candidate = match.Groups[1].Value;
                        // A block referring to itself is not a reference from somewhere else.
                        if (names.Contains(candidate) &&
                            !string.Equals(candidate, block.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            referenced.Add(candidate);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A partial index would report live blocks as dead; report "unknown" instead.
                return null;
            }
            finally
            {
                try { Directory.Delete(temp, true); }
                catch (Exception) { /* temp folder cleanup is best effort */ }
            }

            return referenced;
        }

        // ---- plumbing ------------------------------------------------------

        private void IndexProject()
        {
            _plcs.Clear();
            if (_project == null) return;

            foreach (var device in PlcNavigator.AllDevices(_project))
            {
                var software = PlcNavigator.FindPlcSoftware(device);
                if (software == null) continue;

                var context = new PlcContext { DeviceId = device.Name, Software = software };
                Index(context);
                _plcs[device.Name] = context;
            }
        }

        private static void Index(PlcContext context)
        {
            PlcNavigator.Index(context);
        }

        private List<ExportTarget> ResolveExportTargets(PlcContext plc, IReadOnlyList<string> blockPaths)
        {
            if (blockPaths == null || blockPaths.Count == 0)
            {
                return plc.Blocks.Select(p => ExportTarget.For(p.Key, p.Value))
                    .Concat(plc.Types.Select(p => ExportTarget.For(p.Key, p.Value)))
                    .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var targets = new List<ExportTarget>();
            var missing = new List<string>();

            foreach (var requested in blockPaths)
            {
                var blockMatch = plc.Blocks.FirstOrDefault(p => Matches(p.Key, p.Value.Name, requested));
                if (blockMatch.Value != null)
                {
                    targets.Add(ExportTarget.For(blockMatch.Key, blockMatch.Value));
                    continue;
                }

                var typeMatch = plc.Types.FirstOrDefault(p => Matches(p.Key, p.Value.Name, requested));
                if (typeMatch.Value != null)
                {
                    targets.Add(ExportTarget.For(typeMatch.Key, typeMatch.Value));
                    continue;
                }

                missing.Add(requested);
            }

            if (missing.Count > 0)
            {
                throw new KeyNotFoundException("Unknown block(s) on '" + plc.DeviceId + "': " +
                    string.Join(", ", missing));
            }
            return targets;
        }

        private static bool Matches(string path, string name, string requested)
        {
            return string.Equals(path, requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, requested, StringComparison.OrdinalIgnoreCase);
        }

        private static string FolderOf(string path)
        {
            var index = path.LastIndexOf('/');
            return index < 0 ? string.Empty : path.Substring(0, index);
        }

        /// <summary>Block names may contain characters Windows rejects in a file name.</summary>
        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var buffer = name.ToCharArray();
            for (var i = 0; i < buffer.Length; i++)
            {
                if (Array.IndexOf(invalid, buffer[i]) >= 0) buffer[i] = '_';
            }
            return new string(buffer);
        }

        private void RequireConnected()
        {
            if (_portal == null) throw new InvalidOperationException("Not connected. Call session.connect first.");
        }

        private void RequireProject()
        {
            RequireConnected();
            if (_project == null) throw new InvalidOperationException("No project is open. Call project.open first.");
        }

        private PlcContext RequirePlc(string deviceId)
        {
            RequireProject();

            PlcContext plc;
            if (_plcs.TryGetValue(deviceId, out plc)) return plc;

            throw new KeyNotFoundException("No PLC device named '" + deviceId + "'. Devices with PLC software: " +
                (_plcs.Count == 0 ? "(none)" : string.Join(", ", _plcs.Keys)));
        }

        public void Dispose()
        {
            Disconnect();
        }

        /// <summary>A block or a UDT, addressed uniformly for export.</summary>
        private sealed class ExportTarget
        {
            public string Path;
            public string Name;
            public PlcBlock Block;
            public PlcType Type;
            public bool KnowHowProtected;
            public bool Consistent;

            public static ExportTarget For(string path, PlcBlock block)
            {
                return new ExportTarget
                {
                    Path = path,
                    Name = block.Name,
                    Block = block,
                    KnowHowProtected = block.Prop("IsKnowHowProtected", false),
                    Consistent = block.Prop("IsConsistent", true),
                };
            }

            public static ExportTarget For(string path, PlcType type)
            {
                return new ExportTarget
                {
                    Path = path,
                    Name = type.Name,
                    Type = type,
                    KnowHowProtected = type.Prop("IsKnowHowProtected", false),
                    Consistent = type.Prop("IsConsistent", true),
                };
            }
        }
    }
}
