using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Cli
{
    /// <summary>
    /// Scriptable front end over the bridge. Everything the desktop UI can do is reachable
    /// here too, so the same operations run unattended in CI.
    /// Exit codes: 0 success, 1 the operation reported failures, 2 usage or transport error.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }

            var options = Options.Parse(args);

            try
            {
                using var client = new TiaClient();
                client.Bridge.Log += (_, e) => Console.Error.WriteLine(e.Line);
                client.Bridge.Progress += (_, e) =>
                {
                    var p = e.Progress;
                    Console.Error.WriteLine($"  [{p.Current}/{p.Total}] {p.Operation}: {p.Message}");
                };

                client.Start(options.Get("bridge"), options.Has("mock"));

                return options.Command switch
                {
                    "doctor" => await Doctor(client),
                    "devices" => await Devices(client, options),
                    "blocks" => await Blocks(client, options),
                    "export" => await Export(client, options),
                    "import" => await Import(client, options),
                    "tags" => await Tags(client, options),
                    "compile" => await Compile(client, options),
                    "inspect" => await Inspect(client, options),
                    "vci" => await Vci(client, options),
                    _ => Fail($"Unknown command '{options.Command}'. Run 'tia help' for the list."),
                };
            }
            catch (BridgeRpcException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                if (ex.Data2 != null) Console.Error.WriteLine(ex.Data2);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        // ---- commands ------------------------------------------------------

        private static async Task<int> Doctor(TiaClient client)
        {
            var report = await client.DoctorAsync();

            Console.WriteLine("TIA Openness environment report");
            Console.WriteLine($"  machine : {report.MachineName}");
            Console.WriteLine($"  user    : {report.UserName}");
            Console.WriteLine($"  process : {(report.Is64BitProcess ? "x64" : "x86")}, CLR {report.ClrVersion}");
            Console.WriteLine();

            foreach (var check in report.Checks)
            {
                var mark = check.Status switch
                {
                    CheckStatus.Pass => "[ OK ]",
                    CheckStatus.Warn => "[WARN]",
                    _ => "[FAIL]",
                };
                Console.WriteLine($"{mark} {check.Title}");
                if (!string.IsNullOrWhiteSpace(check.Detail)) Console.WriteLine($"       {check.Detail}");
                if (!string.IsNullOrWhiteSpace(check.Remedy)) Console.WriteLine($"       -> {check.Remedy}");
            }

            if (report.Installations.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Installations:");
                foreach (var i in report.Installations)
                {
                    Console.WriteLine($"  V{i.Version,-6} {i.EngineeringDllPath}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(report.CanRunOpenness
                ? "Verdict: this machine can run Openness."
                : "Verdict: Openness cannot run here yet - fix the [FAIL] items above.");
            return report.CanRunOpenness ? 0 : 1;
        }

        private static async Task<int> Devices(TiaClient client, Options o)
        {
            await OpenSession(client, o);
            var devices = await client.ListDevicesAsync();

            Console.WriteLine($"{"ID",-14} {"CATEGORY",-8} {"ARTICLE",-22} FIRMWARE");
            foreach (var d in devices)
            {
                Console.WriteLine($"{d.Id,-14} {d.Category,-8} {d.ArticleNumber,-22} {d.FirmwareVersion}");
            }
            Console.WriteLine($"\n{devices.Count} device(s).");
            return 0;
        }

        private static async Task<int> Blocks(TiaClient client, Options o)
        {
            await OpenSession(client, o);
            var deviceId = o.Require("device");
            var blocks = await client.ListBlocksAsync(deviceId, o.Has("system"));

            Console.WriteLine($"{"KIND",-11} {"NO",-6} {"LANG",-6} PATH");
            foreach (var b in blocks.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase))
            {
                var flags = (b.IsKnowHowProtected ? " [protected]" : string.Empty)
                          + (b.IsConsistent ? string.Empty : " [inconsistent]");
                Console.WriteLine($"{b.Kind,-11} {b.Number?.ToString(CultureInfo.InvariantCulture) ?? "-",-6} " +
                                  $"{b.ProgrammingLanguage ?? "-",-6} {b.Path}{flags}");
            }
            Console.WriteLine($"\n{blocks.Count} block(s).");
            return 0;
        }

        private static async Task<int> Export(TiaClient client, Options o)
        {
            await OpenSession(client, o);

            var format = o.Get("format", "SimaticMl");
            if (!Enum.TryParse<ExportFormat>(format, true, out var parsedFormat))
            {
                return Fail($"--format must be SimaticMl or Source, got '{format}'.");
            }

            var result = await client.ExportBlocksAsync(
                o.Require("device"),
                o.GetList("blocks"),
                o.Require("out"),
                parsedFormat,
                !o.Has("flat"));

            foreach (var item in result.Items.Where(i => !i.Succeeded))
            {
                Console.Error.WriteLine($"  FAILED {item.BlockPath}: {item.Error}");
            }

            Console.WriteLine($"Exported {result.Succeeded}/{result.Requested} block(s) to {result.OutputDirectory}");
            if (result.Failed > 0) Console.WriteLine($"{result.Failed} failed.");
            return result.Failed == 0 ? 0 : 1;
        }

        private static async Task<int> Import(TiaClient client, Options o)
        {
            await OpenSession(client, o);
            var result = await client.ImportBlocksAsync(o.Require("device"), o.GetList("files"), o.Has("overwrite"));

            foreach (var item in result.Items.Where(i => !i.Succeeded))
            {
                Console.Error.WriteLine($"  FAILED {item.FilePath}: {item.Error}");
            }

            Console.WriteLine($"Imported {result.Succeeded}/{result.Requested} file(s).");
            if (o.Has("save")) await client.SaveProjectAsync();
            return result.Failed == 0 ? 0 : 1;
        }

        private static async Task<int> Tags(TiaClient client, Options o)
        {
            await OpenSession(client, o);
            var deviceId = o.Require("device");
            var table = o.Get("table");

            if (table == null)
            {
                var tables = await client.ListTagTablesAsync(deviceId);
                Console.WriteLine($"{"TAGS",-6} TABLE");
                foreach (var t in tables) Console.WriteLine($"{t.TagCount,-6} {t.Name}{(t.IsDefault ? " (default)" : "")}");
                return 0;
            }

            var tags = await client.ListTagsAsync(deviceId, table);
            Console.WriteLine($"{"NAME",-24} {"TYPE",-10} {"ADDRESS",-10} COMMENT");
            foreach (var t in tags)
            {
                Console.WriteLine($"{t.Name,-24} {t.DataType,-10} {t.LogicalAddress,-10} {t.Comment}");
            }
            return 0;
        }

        private static async Task<int> Compile(TiaClient client, Options o)
        {
            await OpenSession(client, o);
            var result = await client.CompileAsync(o.Require("device"), !o.Has("hardware"));

            foreach (var m in Flatten(result.Messages))
            {
                var mark = m.Severity switch
                {
                    CompileSeverity.Error => "ERROR",
                    CompileSeverity.Warning => "WARN ",
                    _ => "INFO ",
                };
                Console.WriteLine($"{mark} {m.Target}: {m.Description}{(m.ErrorCode != null ? $" ({m.ErrorCode})" : "")}");
            }

            Console.WriteLine($"\n{result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s) " +
                              $"in {result.Duration.TotalSeconds:F1}s");
            if (o.Has("save") && result.Succeeded) await client.SaveProjectAsync();
            return result.Succeeded ? 0 : 1;
        }

        private static async Task<int> Inspect(TiaClient client, Options o)
        {
            await OpenSession(client, o);
            var report = await client.InspectAsync(o.Require("device"), o.Get("name-pattern"));

            foreach (var group in report.Findings.GroupBy(f => f.RuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"\n{group.Key} ({group.Count()})");
                foreach (var f in group)
                {
                    Console.WriteLine($"  {f.Severity,-4} {f.Target}: {f.Message}");
                    if (!string.IsNullOrWhiteSpace(f.Suggestion)) Console.WriteLine($"        -> {f.Suggestion}");
                }
            }

            var errors = report.Findings.Count(f => f.Severity == CheckStatus.Fail);
            Console.WriteLine($"\nScanned {report.BlocksScanned} block(s): " +
                              $"{report.Findings.Count} finding(s), {errors} blocking.");
            return errors == 0 ? 0 : 1;
        }

        // ---- version control ------------------------------------------------

        /// <summary>
        /// The V21 Version Control Interface. Every mutating sub-command defaults to a dry run
        /// and needs <c>--apply</c>, because <c>pull</c> overwrites blocks in the open project
        /// and <c>map</c> writes into the project structure.
        /// </summary>
        private static async Task<int> Vci(TiaClient client, Options o)
        {
            await OpenSession(client, o);

            if (!await client.VcSupportedAsync())
            {
                Console.Error.WriteLine(
                    "error: this project has no Version Control Interface. VCI needs TIA Portal V21 or later; " +
                    "on an older version use 'tia export --format Source' for a text snapshot.");
                return 1;
            }

            var workspace = o.Get("workspace");
            var apply = o.Has("apply");

            switch (o.SubCommand)
            {
                case "workspaces":
                    return await VciWorkspaces(client);
                case "create":
                    return await VciCreate(client, o);
                case "map":
                    return await VciMap(client, o, workspace, apply);
                case "status":
                    return await VciStatus(client, o, workspace);
                case "push":
                    return await VciSync(client, workspace, SyncDirection.ProjectToWorkspace, apply);
                case "pull":
                    return await VciSync(client, workspace, SyncDirection.WorkspaceToProject, apply);
                default:
                    return Fail("vci needs a sub-command: workspaces | create | map | status | push | pull.");
            }
        }

        private static async Task<int> VciWorkspaces(TiaClient client)
        {
            var workspaces = await client.VcListWorkspacesAsync();
            if (workspaces.Count == 0)
            {
                Console.WriteLine("No workspace yet. Create one:  tia vci create --name git --folder D:\\repo\\plc");
                return 0;
            }

            Console.WriteLine($"{"OBJECTS",-8} {"NAME",-18} FOLDER");
            foreach (var w in workspaces)
            {
                Console.WriteLine($"{w.MappedObjectCount,-8} {w.Name,-18} {w.RootPath}");
            }
            return 0;
        }

        private static async Task<int> VciCreate(TiaClient client, Options o)
        {
            var workspace = await client.VcCreateWorkspaceAsync(o.Require("name"), o.Require("folder"));
            Console.WriteLine($"Created workspace '{workspace.Name}' at {workspace.RootPath}");
            Console.WriteLine("Next:  tia vci map --apply     (maps the project's objects into it)");
            return 0;
        }

        private static async Task<int> VciMap(TiaClient client, Options o, string workspace, bool apply)
        {
            var result = await client.VcMapProjectAsync(workspace, o.Get("device"), dryRun: !apply);

            foreach (var item in result.Items.Where(i => i.Outcome is "failed" or "unsupported"))
            {
                Console.Error.WriteLine($"  {item.Outcome.ToUpperInvariant()} {item.Target}: {item.Error}");
            }

            Console.WriteLine(result.DryRun
                ? $"DRY RUN: {result.Mapped} object(s) would be mapped, {result.AlreadyMapped} already mapped, " +
                  $"{result.Unsupported} unsupported. Re-run with --apply to do it."
                : $"Mapped {result.Mapped}, already mapped {result.AlreadyMapped}, " +
                  $"unsupported {result.Unsupported}, failed {result.Failed}.");

            if (result.Truncated) Console.WriteLine("The walk hit its node budget; run again to continue.");
            return result.Failed == 0 ? 0 : 1;
        }

        private static async Task<int> VciStatus(TiaClient client, Options o, string workspace)
        {
            var report = await client.VcStatusAsync(workspace, changedOnly: !o.Has("all"));

            foreach (var item in report.Items)
            {
                Console.WriteLine($"{item.CompareState,-21} {item.Name}");
            }

            Console.WriteLine();
            Console.WriteLine($"Workspace '{report.WorkspaceName}' ({report.RootPath})");
            Console.WriteLine(report.InSync
                ? $"{report.Total} mapped object(s), all in sync - nothing to commit."
                : $"{report.Total} mapped object(s), {report.Differing} differ. " +
                  "Write them out with:  tia vci push --apply");

            // Exit 1 on drift, so a CI job can fail when someone edited the project without committing.
            return report.InSync ? 0 : 1;
        }

        private static async Task<int> VciSync(TiaClient client, string workspace, SyncDirection direction, bool apply)
        {
            var result = await client.VcSyncAsync(workspace, direction, dryRun: !apply);

            foreach (var item in result.Items.Where(i => i.Error != null))
            {
                Console.Error.WriteLine($"  FAILED {item.Name}: {item.Error}");
            }

            if (result.DryRun)
            {
                Console.WriteLine($"DRY RUN: {result.Synchronized} object(s) would be synchronized {direction}, " +
                                  $"{result.SkippedEqual} already equal. Re-run with --apply to do it.");
                return 0;
            }

            Console.WriteLine($"{result.Synchronized} synchronized, {result.Failed} failed, " +
                              $"{result.SkippedEqual} already equal.");

            Console.WriteLine(direction == SyncDirection.ProjectToWorkspace
                ? $"The text files in {result.RootPath} are current - commit them:  git -C \"{result.RootPath}\" add -A && git -C \"{result.RootPath}\" commit"
                : "The project now holds the workspace's version. Compile and save it:  tia compile --device <id> --save");

            return result.Failed == 0 ? 0 : 1;
        }

        // ---- helpers -------------------------------------------------------

        private static async Task OpenSession(TiaClient client, Options o)
        {
            await client.ConnectAsync(!o.Has("headless"), attachToRunning: true, version: o.Get("version"));

            var project = o.Get("project");
            if (project != null) await client.OpenProjectAsync(project);
        }

        private static IEnumerable<CompileMessage> Flatten(IEnumerable<CompileMessage> messages)
        {
            foreach (var m in messages)
            {
                yield return m;
                if (m.Children == null) continue;
                foreach (var child in Flatten(m.Children)) yield return child;
            }
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"error: {message}");
            return 2;
        }

        private static bool IsHelp(string arg)
        {
            return arg is "help" or "-h" or "--help" or "/?";
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"tia - TIA Portal Openness command line

USAGE
  tia <command> [options]

COMMANDS
  doctor                          Check every Openness precondition on this machine
  devices                         List devices in the open project
  blocks    --device <id>         List software blocks
  export    --device <id> --out <dir> [--blocks a,b] [--format SimaticMl|Source] [--flat]
  import    --device <id> --files a.scl,b.xml [--overwrite] [--save]
  tags      --device <id> [--table <name>]
  compile   --device <id> [--hardware] [--save]
  inspect   --device <id> [--name-pattern <regex>]

VERSION CONTROL (TIA Portal V21+)
  vci workspaces                  List the project's VCI workspaces
  vci create --name <n> --folder <dir>
                                  Create a workspace over an existing folder (your Git working tree)
  vci map    [--device <id>] [--apply]
                                  Map the project's objects into the workspace
  vci status [--all]              Per-object diff between project and workspace files
  vci push   [--apply]            Write the project out as text, ready to commit
  vci pull   [--apply]            Read the text files back INTO the project (overwrites blocks)

  Mutating vci sub-commands are a DRY RUN unless --apply is given.
  'vci status' exits 1 when anything differs, so CI can fail on uncommitted project edits.

COMMON OPTIONS
  --project <path>   Open this .ap21 project first
  --version <ver>    Bind a specific Openness version, e.g. 21.0
  --headless         Start TIA Portal without its window (cannot show the trust dialog)
  --mock             Run against the built-in fake project; no TIA Portal needed
  --bridge <path>    Explicit path to TiaOpenness.Bridge.exe

EXIT CODES
  0 success   1 the operation reported failures   2 usage or transport error

EXAMPLES
  tia doctor
  tia --mock devices --project Demo.ap21
  tia export --project D:\p\Line.ap21 --device PLC_1 --out D:\repo\plc --format Source
  tia compile --project D:\p\Line.ap21 --device PLC_1 --save

  Put a project under Git (V21):
    tia vci create --project D:\p\Line.ap21 --name git --folder D:\repo\plc
    tia vci map    --project D:\p\Line.ap21 --apply
    tia vci push   --project D:\p\Line.ap21 --apply
    git -C D:\repo\plc add -A && git -C D:\repo\plc commit -m ""PLC snapshot""");
        }
    }

    /// <summary>Minimal <c>--key value</c> / <c>--flag</c> parser; the first bare token is the command.</summary>
    internal sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; } = string.Empty;

        /// <summary>Second bare token, for grouped commands such as <c>tia vci status</c>.</summary>
        public string SubCommand { get; private set; } = string.Empty;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (o.Command.Length == 0) o.Command = arg;
                    else if (o.SubCommand.Length == 0) o.SubCommand = arg;
                    continue;
                }

                var key = arg.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    o._values[key] = args[++i];
                }
                else
                {
                    o._flags.Add(key);
                }
            }
            return o;
        }

        public bool Has(string key) => _flags.Contains(key);

        public string Get(string key, string fallback = null)
            => _values.TryGetValue(key, out var value) ? value : fallback;

        public string Require(string key)
            => _values.TryGetValue(key, out var value)
                ? value
                : throw new ArgumentException($"Missing required option --{key}.");

        /// <summary>Comma-separated list, e.g. <c>--blocks Motion/FB_Axis,Safety/FC_EStop</c>.</summary>
        public List<string> GetList(string key)
            => Get(key) is { } raw
                ? raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
                : new List<string>();
    }
}
