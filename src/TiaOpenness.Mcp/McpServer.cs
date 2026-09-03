using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TiaOpenness.Mcp;

/// <summary>One callable tool: its schema and the delegate that runs it.</summary>
public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }
    public required Func<JsonObject, CancellationToken, Task<string>> Handler { get; init; }

    /// <summary>True when the tool changes the TIA project. Gated behind --allow-write.</summary>
    public bool Mutates { get; init; }
}

/// <summary>
/// A minimal Model Context Protocol server over stdio: newline-delimited JSON-RPC 2.0 on
/// stdin/stdout, diagnostics on stderr. Hand-rolled rather than taken from an SDK so the
/// wire behaviour is pinned and testable from a shell script.
/// </summary>
public sealed class McpServer
{
    private const string FallbackProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, McpTool> _tools = new(StringComparer.Ordinal);
    private readonly TextWriter _stdout;
    private readonly TextReader _stdin;
    private readonly string _serverName;
    private readonly string _serverVersion;

    public McpServer(string serverName, string serverVersion, TextReader? stdin = null, TextWriter? stdout = null)
    {
        _serverName = serverName;
        _serverVersion = serverVersion;
        _stdin = stdin ?? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _stdout = stdout ?? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
    }

    public void Add(McpTool tool) => _tools[tool.Name] = tool;

    public async Task RunAsync(CancellationToken cancellation = default)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var line = await _stdin.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;

            JsonObject request;
            try
            {
                request = JsonNode.Parse(line)?.AsObject()
                          ?? throw new JsonException("Frame is not a JSON object.");
            }
            catch (JsonException ex)
            {
                Write(Error(null, -32700, "Parse error: " + ex.Message));
                continue;
            }

            // Notifications carry no id and must never be answered.
            var id = request["id"];
            var method = request["method"]?.GetValue<string>();

            if (method is null)
            {
                if (id is not null) Write(Error(id, -32600, "Request is missing 'method'."));
                continue;
            }

            try
            {
                var result = await DispatchAsync(method, request["params"]?.AsObject(), cancellation)
                    .ConfigureAwait(false);

                if (id is null) continue;   // notification
                Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
            }
            catch (McpMethodNotFoundException ex)
            {
                if (id is not null) Write(Error(id, -32601, ex.Message));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[mcp] {method} failed: {ex}");
                if (id is not null) Write(Error(id, -32603, ex.Message));
            }
        }
    }

    private async Task<JsonNode?> DispatchAsync(string method, JsonObject? parameters, CancellationToken cancellation)
    {
        switch (method)
        {
            case "initialize":
                return Initialize(parameters);

            case "notifications/initialized":
            case "notifications/cancelled":
                return null;

            case "ping":
                return new JsonObject();

            case "tools/list":
                return ListTools();

            case "tools/call":
                return await CallToolAsync(parameters, cancellation).ConfigureAwait(false);

            default:
                throw new McpMethodNotFoundException($"Method '{method}' is not supported by this server.");
        }
    }

    private JsonNode Initialize(JsonObject? parameters)
    {
        // Echo the client's protocol version when it names one, so a newer client is not
        // downgraded by a hardcoded constant.
        var requested = parameters?["protocolVersion"]?.GetValue<string>();

        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(requested) ? FallbackProtocolVersion : requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = _serverName, ["version"] = _serverVersion },
        };
    }

    private JsonNode ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? parameters, CancellationToken cancellation)
    {
        var name = parameters?["name"]?.GetValue<string>()
                   ?? throw new ArgumentException("tools/call requires a 'name'.");

        if (!_tools.TryGetValue(name, out var tool))
        {
            throw new McpMethodNotFoundException($"No tool named '{name}'.");
        }

        var arguments = parameters?["arguments"]?.AsObject() ?? new JsonObject();

        try
        {
            var text = await tool.Handler(arguments, cancellation).ConfigureAwait(false);
            return Content(text, isError: false);
        }
        catch (Exception ex)
        {
            // MCP convention: tool failures come back as a result with isError, not a
            // protocol error, so the model can read the message and correct itself.
            Console.Error.WriteLine($"[mcp] tool {name} failed: {ex}");
            return Content(ex.Message, isError: true);
        }
    }

    private static JsonNode Content(string text, bool isError) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private void Write(JsonNode payload)
    {
        _stdout.Write(payload.ToJsonString(Json));
        _stdout.Write('\n');
        _stdout.Flush();
    }
}

/// <summary>The client asked for a method or tool this server does not have.</summary>
public sealed class McpMethodNotFoundException(string message) : Exception(message);
