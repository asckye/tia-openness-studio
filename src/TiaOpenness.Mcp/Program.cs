using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaOpenness.Client;

namespace TiaOpenness.Mcp;

/// <summary>
/// MCP entry point. Wire it into an MCP client (Claude Desktop, Cursor, VS Code) as a stdio
/// server. stdout carries protocol frames only, so every diagnostic goes to stderr.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            Console.Error.WriteLine("""
                TiaOpenness.Mcp - Model Context Protocol server for TIA Portal Openness

                USAGE
                  TiaOpenness.Mcp [--allow-write] [--mock] [--bridge <path>] [--openness-version <ver>]

                OPTIONS
                  --allow-write       Also expose the tools that modify the project (import, save).
                                      Omitted by default: read tools cannot damage a project.
                  --mock              Serve a synthetic project; no TIA Portal needed. Use this to
                                      try the wiring before pointing it at real engineering data.
                  --bridge <path>     Explicit path to TiaOpenness.Bridge.exe.
                  --openness-version  Bind a specific Openness version, e.g. 21.0.

                CLIENT CONFIGURATION (stdio)
                  {
                    "mcpServers": {
                      "tia": {
                        "command": "C:\\path\\to\\TiaOpenness.Mcp.exe",
                        "args": ["--allow-write"]
                      }
                    }
                  }
                """);
            return 0;
        }

        var allowWrite = args.Contains("--allow-write");
        var mock = args.Contains("--mock");
        var bridgePath = Value(args, "--bridge");

        using var client = new TiaClient();
        client.Bridge.Log += (_, e) => Console.Error.WriteLine($"[bridge] {e.Line}");

        try
        {
            client.Start(bridgePath, mock);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mcp] cannot start the bridge: {ex.Message}");
            return 2;
        }

        Console.Error.WriteLine($"[mcp] ready; write tools {(allowWrite ? "ENABLED" : "disabled")}" +
                                $"{(mock ? ", mock backend" : string.Empty)}");

        var server = new McpServer("tia-openness", ThisVersion());
        new TiaTools(client, allowWrite).RegisterAll(server);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        await server.RunAsync(cancellation.Token);
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string ThisVersion()
        => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
