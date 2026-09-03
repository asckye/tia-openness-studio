using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Mcp;

/// <summary>
/// The TIA Portal tool surface exposed to an MCP client.
///
/// Read tools are always available. Tools that change the project are registered only when
/// the server is started with --allow-write, because an engineering project is not a place
/// to discover that a model misunderstood an instruction.
/// </summary>
public sealed class TiaTools(TiaClient client, bool allowWrite)
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public void RegisterAll(McpServer server)
    {
        server.Add(Doctor());
        server.Add(Connect());
        server.Add(OpenProject());
        server.Add(ListDevices());
        server.Add(ListBlocks());
        server.Add(ListTags());
        server.Add(ExportBlocks());
        server.Add(Compile());
        server.Add(Inspect());

        server.Add(VcWorkspaces());
        server.Add(VcStatus());
        server.Add(VcPush());

        if (!allowWrite) return;
        server.Add(ImportBlocks());
        server.Add(SaveProject());
        server.Add(VcCreateWorkspace());
        server.Add(VcMap());
        server.Add(VcPull());
    }

    // ---- version control ---------------------------------------------------
    //
    // Read tier: listing, status, and push - push writes text files on disk but leaves the TIA
    // project untouched, exactly like tia_export_blocks.
    // Write tier: creating a workspace and mapping both store configuration inside the project,
    // and pull overwrites blocks in it.

    private McpTool VcWorkspaces() => new()
    {
        Name = "tia_vc_workspaces",
        Description = "List the project's Version Control Interface workspaces: name, folder on disk, " +
                      "and how many objects are mapped. A workspace is the plain-text mirror of the " +
                      "project that Git can diff and commit. Read-only. Requires TIA Portal V21+.",
        InputSchema = Schema(),
        Handler = async (_, ct) =>
        {
            if (!await client.VcSupportedAsync(ct)) return VcUnsupported;

            var workspaces = await client.VcListWorkspacesAsync(ct);
            return workspaces.Count == 0
                ? "No workspace yet. Create one with tia_vc_create_workspace, then map the project " +
                  "into it with tia_vc_map."
                : Serialize(workspaces);
        },
    };

    private McpTool VcStatus() => new()
    {
        Name = "tia_vc_status",
        Description = "Per-object diff between the TIA project and the workspace's text files. " +
                      "This is what to read before committing: it names exactly which blocks changed. " +
                      "States: Equal (in sync), Unequal (differ), WorkspaceFileMissing (never written), " +
                      "Unknown. Read-only.",
        InputSchema = Schema(
            Prop("workspaceName", "string", "Which workspace. Omit for the project's first one."),
            Prop("changedOnly", "boolean", "Only objects that are not in sync. Default true.")),
        Handler = async (args, ct) =>
        {
            if (!await client.VcSupportedAsync(ct)) return VcUnsupported;

            var report = await client.VcStatusAsync(
                Str(args, "workspaceName"), Bool(args, "changedOnly", true), ct);

            var text = new StringBuilder();
            text.AppendLine($"Workspace '{report.WorkspaceName}' ({report.RootPath}): " +
                            $"{report.Total} mapped object(s), {report.Differing} differ.");
            foreach (var item in report.Items)
            {
                text.AppendLine($"{item.CompareState}: {item.Name}");
            }
            text.AppendLine(report.InSync
                ? "In sync - nothing to commit."
                : "Write the changes out with tia_vc_push, then commit from the workspace folder.");
            return text.ToString();
        },
    };

    private McpTool VcPush() => new()
    {
        Name = "tia_vc_push",
        Description = "Write the project's changed objects out as text files, ready for 'git commit'. " +
                      "Does not modify the TIA project. Objects already in sync are skipped - TIA " +
                      "refuses to synchronize them. DEFAULTS TO dryRun=true: the default call only " +
                      "reports what would be written.",
        InputSchema = Schema(
            Prop("workspaceName", "string", "Which workspace. Omit for the project's first one."),
            Prop("dryRun", "boolean", "Report without writing. Default true; pass false to write.")),
        Handler = (args, ct) => VcSync(args, SyncDirection.ProjectToWorkspace, ct),
    };

    private McpTool VcCreateWorkspace() => new()
    {
        Name = "tia_vc_create_workspace",
        Description = "Create a VCI workspace over an existing folder - normally a Git working tree. " +
                      "MODIFIES THE PROJECT: the workspace is stored in it. Creating a workspace maps " +
                      "nothing; call tia_vc_map afterwards.",
        Mutates = true,
        InputSchema = Schema(
            Prop("name", "string", "Workspace name shown in the TIA project tree, e.g. 'git'."),
            Prop("folderPath", "string", "Existing folder for the text files, e.g. 'D:\\\\repos\\\\line-plc'.")
            ).Required("name", "folderPath"),
        Handler = async (args, ct) =>
        {
            if (!await client.VcSupportedAsync(ct)) return VcUnsupported;

            var workspace = await client.VcCreateWorkspaceAsync(
                Require(args, "name"), Require(args, "folderPath"), ct);

            return $"Created workspace '{workspace.Name}' at {workspace.RootPath}. " +
                   "Next: tia_vc_map with dryRun=false to map the project's objects into it.";
        },
    };

    private McpTool VcMap() => new()
    {
        Name = "tia_vc_map",
        Description = "Map the project's objects into a workspace, so they can be exported as text. " +
                      "Walks the whole project and maps everything VCI supports in one call - coarse " +
                      "first, so a device that can be mapped as a unit is not split into blocks. " +
                      "MODIFIES THE PROJECT. DEFAULTS TO dryRun=true.",
        Mutates = true,
        InputSchema = Schema(
            Prop("workspaceName", "string", "Which workspace. Omit for the project's first one."),
            Prop("deviceId", "string", "Map only this device. Omit to map the whole project."),
            Prop("dryRun", "boolean", "Report without mapping. Default true; pass false to map.")),
        Handler = async (args, ct) =>
        {
            if (!await client.VcSupportedAsync(ct)) return VcUnsupported;

            var result = await client.VcMapProjectAsync(
                Str(args, "workspaceName"), Str(args, "deviceId"), Bool(args, "dryRun", true), ct);

            var text = new StringBuilder();
            text.AppendLine(result.DryRun
                ? $"DRY RUN: {result.Mapped} would be mapped, {result.AlreadyMapped} already mapped, " +
                  $"{result.Unsupported} unsupported. Call again with dryRun=false to do it."
                : $"Mapped {result.Mapped}, already mapped {result.AlreadyMapped}, " +
                  $"unsupported {result.Unsupported}, failed {result.Failed}.");

            foreach (var item in result.Items.Where(i => i.Outcome is "failed"))
            {
                text.AppendLine($"FAILED {item.Target}: {item.Error}");
            }
            if (result.Truncated) text.AppendLine("The walk hit its node budget; call again to continue.");
            return text.ToString();
        },
    };

    private McpTool VcPull() => new()
    {
        Name = "tia_vc_pull",
        Description = "Read the workspace's text files back INTO the TIA project - use after a git " +
                      "pull, or to restore a reviewed version. OVERWRITES BLOCKS IN THE OPEN PROJECT " +
                      "and cannot be undone. Compile and save afterwards. DEFAULTS TO dryRun=true.",
        Mutates = true,
        InputSchema = Schema(
            Prop("workspaceName", "string", "Which workspace. Omit for the project's first one."),
            Prop("dryRun", "boolean", "Report without changing the project. Default true.")),
        Handler = (args, ct) => VcSync(args, SyncDirection.WorkspaceToProject, ct),
    };

    private async Task<string> VcSync(JsonObject args, SyncDirection direction, CancellationToken ct)
    {
        if (!await client.VcSupportedAsync(ct)) return VcUnsupported;

        var result = await client.VcSyncAsync(
            Str(args, "workspaceName"), direction, Bool(args, "dryRun", true), ct);

        var text = new StringBuilder();
        text.AppendLine(result.DryRun
            ? $"DRY RUN: {result.Synchronized} object(s) would be synchronized {direction}, " +
              $"{result.SkippedEqual} already equal. Call again with dryRun=false to do it."
            : $"{result.Synchronized} synchronized, {result.Failed} failed, " +
              $"{result.SkippedEqual} already equal.");

        foreach (var item in result.Items.Where(i => i.Error is not null))
        {
            text.AppendLine($"FAILED {item.Name}: {item.Error}");
        }

        if (!result.DryRun)
        {
            text.AppendLine(direction == SyncDirection.ProjectToWorkspace
                ? $"The text files in {result.RootPath} are current - commit them from that folder."
                : "The project now holds the workspace's version. Compile it, then save.");
        }
        return text.ToString();
    }

    private const string VcUnsupported =
        "This project has no Version Control Interface. VCI requires TIA Portal V21 or later; " +
        "on an older version use tia_export_blocks with format=Source for a text snapshot instead.";

    // ---- read tools --------------------------------------------------------

    private McpTool Doctor() => new()
    {
        Name = "tia_doctor",
        Description = "Check whether this machine can drive TIA Portal through Openness: process bitness, " +
                      ".NET Framework, installed Openness versions, and the 'Siemens TIA Openness' group " +
                      "membership. Run this first when anything fails to connect.",
        InputSchema = Schema(),
        Handler = async (_, ct) =>
        {
            var report = await client.DoctorAsync(ct);
            var text = new StringBuilder();
            text.AppendLine($"machine {report.MachineName}, user {report.UserName}, " +
                            $"{(report.Is64BitProcess ? "x64" : "x86")}");

            foreach (var check in report.Checks)
            {
                text.AppendLine($"{check.Status}: {check.Title} - {check.Detail}");
                if (!string.IsNullOrWhiteSpace(check.Remedy)) text.AppendLine($"    remedy: {check.Remedy}");
            }

            foreach (var install in report.Installations)
            {
                text.AppendLine($"installed: V{install.Version} at {install.EngineeringDllPath}");
            }

            text.AppendLine(report.CanRunOpenness
                ? "Verdict: Openness can run here."
                : "Verdict: Openness cannot run here yet.");
            return text.ToString();
        },
    };

    private McpTool Connect() => new()
    {
        Name = "tia_connect",
        Description = "Attach to a running TIA Portal, or start one. Call this before any other project tool. " +
                      "Prefer withUserInterface=true the first time on a machine: TIA shows a one-off trust " +
                      "dialog that a headless instance cannot display.",
        InputSchema = Schema(
            Prop("withUserInterface", "boolean", "Show the TIA Portal window. Default true."),
            Prop("attachToRunning", "boolean", "Reuse an already-running instance when there is one. Default true."),
            Prop("version", "string", "Openness version to bind, e.g. '21.0'. Omit for the newest installed.")),
        Handler = async (args, ct) =>
        {
            var state = await client.ConnectAsync(
                Bool(args, "withUserInterface", true),
                Bool(args, "attachToRunning", true),
                Str(args, "version"), ct);

            var project = state.OpenProject is null ? "none" : $"{state.OpenProject.Name} ({state.OpenProject.Path})";
            return $"connected={state.Connected}, mode={state.Mode}, version={state.OpennessVersion}, project={project}";
        },
    };

    private McpTool OpenProject() => new()
    {
        Name = "tia_open_project",
        Description = "Open a TIA Portal project file (.ap21, .ap20, ...). The project stays open for " +
                      "subsequent calls until tia_connect is called again.",
        InputSchema = Schema(Prop("path", "string", "Full path to the project file.")).Required("path"),
        Handler = async (args, ct) =>
        {
            var project = await client.OpenProjectAsync(Require(args, "path"), ct);
            return Serialize(project);
        },
    };

    private McpTool ListDevices() => new()
    {
        Name = "tia_list_devices",
        Description = "List every device in the open project with its article number, firmware and category " +
                      "(Plc / Hmi / Drive). Use the returned id as deviceId for the other tools.",
        InputSchema = Schema(),
        Handler = async (_, ct) => Serialize(await client.ListDevicesAsync(ct)),
    };

    private McpTool ListBlocks() => new()
    {
        Name = "tia_list_blocks",
        Description = "List the software blocks and UDTs of one PLC, with their folder path, number, language " +
                      "and whether they are consistent or know-how protected.",
        InputSchema = Schema(
            Prop("deviceId", "string", "Device id from tia_list_devices, e.g. 'PLC_1'."),
            Prop("includeSystemBlocks", "boolean", "Include TIA-generated blocks. Default false.")
            ).Required("deviceId"),
        Handler = async (args, ct) => Serialize(
            await client.ListBlocksAsync(Require(args, "deviceId"), Bool(args, "includeSystemBlocks", false), ct)),
    };

    private McpTool ListTags() => new()
    {
        Name = "tia_list_tags",
        Description = "List PLC tag tables, or the tags inside one table when tableName is given.",
        InputSchema = Schema(
            Prop("deviceId", "string", "Device id, e.g. 'PLC_1'."),
            Prop("tableName", "string", "Tag table name. Omit to list the tables themselves.")
            ).Required("deviceId"),
        Handler = async (args, ct) =>
        {
            var deviceId = Require(args, "deviceId");
            var table = Str(args, "tableName");
            return table is null
                ? Serialize(await client.ListTagTablesAsync(deviceId, ct))
                : Serialize(await client.ListTagsAsync(deviceId, table, ct));
        },
    };

    private McpTool ExportBlocks() => new()
    {
        Name = "tia_export_blocks",
        Description = "Export blocks to files on disk. Format SimaticMl writes .xml (works for every block); " +
                      "format Source writes .scl/.db/.udt text, which only works for textual languages. " +
                      "A block must be consistent (compiled) before TIA will export it. " +
                      "This writes files but does not modify the TIA project.",
        InputSchema = Schema(
            Prop("deviceId", "string", "Device id, e.g. 'PLC_1'."),
            Prop("outputDirectory", "string", "Directory to write into; created if missing."),
            ArrayProp("blocks", "Block paths to export, e.g. 'Motion/FB_Axis'. Omit to export everything."),
            Prop("format", "string", "SimaticMl (default) or Source.", ["SimaticMl", "Source"]),
            Prop("preserveFolders", "boolean", "Mirror the TIA folder structure on disk. Default true.")
            ).Required("deviceId", "outputDirectory"),
        Handler = async (args, ct) =>
        {
            var format = Enum.TryParse<ExportFormat>(Str(args, "format"), true, out var parsed)
                ? parsed
                : ExportFormat.SimaticMl;

            var result = await client.ExportBlocksAsync(
                Require(args, "deviceId"),
                StrList(args, "blocks"),
                Require(args, "outputDirectory"),
                format,
                Bool(args, "preserveFolders", true), ct);

            var text = new StringBuilder();
            text.AppendLine($"Exported {result.Succeeded}/{result.Requested} to {result.OutputDirectory}");
            foreach (var item in result.Items.Where(i => !i.Succeeded))
            {
                text.AppendLine($"FAILED {item.BlockPath}: {item.Error}");
            }
            return text.ToString();
        },
    };

    private McpTool Compile() => new()
    {
        Name = "tia_compile",
        Description = "Compile a device and return the diagnostics. Compiling changes the project's build state, " +
                      "but does not alter any block source. Blocks must compile before they can be exported.",
        InputSchema = Schema(
            Prop("deviceId", "string", "Device id, e.g. 'PLC_1'."),
            Prop("softwareOnly", "boolean", "Compile only the software, not the hardware configuration. Default true.")
            ).Required("deviceId"),
        Handler = async (args, ct) =>
        {
            var result = await client.CompileAsync(Require(args, "deviceId"), Bool(args, "softwareOnly", true), ct);

            var text = new StringBuilder();
            text.AppendLine($"{result.State}: {result.ErrorCount} error(s), {result.WarningCount} warning(s) " +
                            $"in {result.Duration.TotalSeconds:F1}s");
            foreach (var message in Flatten(result.Messages).Where(m => m.Severity != CompileSeverity.Information))
            {
                text.AppendLine($"{message.Severity}: {message.Target} - {message.Description}");
            }
            return text.ToString();
        },
    };

    private McpTool Inspect() => new()
    {
        Name = "tia_inspect",
        Description = "Run project hygiene rules over one PLC: naming convention, missing block author, " +
                      "inconsistent blocks, know-how protected blocks, and blocks nothing references. " +
                      "Read-only. The dead-code rule exports the program to a temp folder, so it is slow " +
                      "on a large project - set findUnusedBlocks=false to skip it.",
        InputSchema = Schema(
            Prop("deviceId", "string", "Device id, e.g. 'PLC_1'."),
            Prop("blockNamePattern", "string", "Regex every block name must match, e.g. '^(OB|FB|FC|DB)_'."),
            Prop("requireBlockComment", "boolean", "Flag blocks with no author. Default true."),
            Prop("findUnusedBlocks", "boolean", "Flag unreferenced blocks. Default true, and slow.")
            ).Required("deviceId"),
        Handler = async (args, ct) =>
        {
            var report = await client.InspectAsync(
                Require(args, "deviceId"),
                Str(args, "blockNamePattern"),
                Bool(args, "requireBlockComment", true),
                Bool(args, "findUnusedBlocks", true),
                flagInconsistentBlocks: true, ct);

            var text = new StringBuilder();
            text.AppendLine($"Scanned {report.BlocksScanned} block(s), {report.Findings.Count} finding(s).");
            foreach (var finding in report.Findings)
            {
                text.AppendLine($"{finding.RuleId} [{finding.Severity}] {finding.Target}: {finding.Message}");
            }
            return text.ToString();
        },
    };

    // ---- write tools -------------------------------------------------------

    private McpTool ImportBlocks() => new()
    {
        Name = "tia_import_blocks",
        Description = "Import .xml (SimaticML) or .scl/.db/.udt source files into a PLC. " +
                      "MODIFIES THE PROJECT. Existing blocks are only replaced when overwrite=true. " +
                      "The change is not persisted until tia_save_project runs.",
        Mutates = true,
        InputSchema = Schema(
            Prop("deviceId", "string", "Device id, e.g. 'PLC_1'."),
            ArrayProp("files", "Full paths of the files to import."),
            Prop("overwrite", "boolean", "Replace blocks that already exist. Default false.")
            ).Required("deviceId", "files"),
        Handler = async (args, ct) =>
        {
            var result = await client.ImportBlocksAsync(
                Require(args, "deviceId"), StrList(args, "files"), Bool(args, "overwrite", false), ct);

            var text = new StringBuilder();
            text.AppendLine($"Imported {result.Succeeded}/{result.Requested} file(s). " +
                            "Call tia_save_project to persist.");
            foreach (var item in result.Items.Where(i => !i.Succeeded))
            {
                text.AppendLine($"FAILED {item.FilePath}: {item.Error}");
            }
            return text.ToString();
        },
    };

    private McpTool SaveProject() => new()
    {
        Name = "tia_save_project",
        Description = "Save the open TIA Portal project to disk. MODIFIES THE PROJECT FILE and cannot be undone.",
        Mutates = true,
        InputSchema = Schema(),
        Handler = async (_, ct) =>
        {
            await client.SaveProjectAsync(ct);
            return "Project saved.";
        },
    };

    // ---- helpers -----------------------------------------------------------

    private static IEnumerable<CompileMessage> Flatten(IEnumerable<CompileMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            if (message.Children is null) continue;
            foreach (var child in Flatten(message.Children)) yield return child;
        }
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Pretty);

    private static JsonObject Schema(params (string Name, JsonObject Definition)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, definition) in properties) props[name] = definition;

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
    }

    private static (string, JsonObject) Prop(string name, string type, string description, string[]? enumValues = null)
    {
        var definition = new JsonObject { ["type"] = type, ["description"] = description };
        if (enumValues is not null)
        {
            definition["enum"] = new JsonArray(enumValues.Select(v => (JsonNode)v!).ToArray());
        }
        return (name, definition);
    }

    private static (string, JsonObject) ArrayProp(string name, string description) => (name, new JsonObject
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    });

    private static string Require(JsonObject args, string name)
        => Str(args, name) ?? throw new ArgumentException($"Missing required argument '{name}'.");

    private static string? Str(JsonObject args, string name)
    {
        var node = args[name];
        if (node is null) return null;
        var value = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool Bool(JsonObject args, string name, bool fallback)
    {
        var node = args[name];
        if (node is null) return fallback;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(node.GetValue<string>(), out var parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    private static List<string> StrList(JsonObject args, string name)
    {
        if (args[name] is not JsonArray array) return [];
        return array
            .Where(n => n is not null)
            .Select(n => n!.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}

/// <summary>Marks JSON-schema properties as required, so tool schemas read as one expression.</summary>
internal static class SchemaExtensions
{
    public static JsonObject Required(this JsonObject schema, params string[] names)
    {
        schema["required"] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray());
        return schema;
    }
}
