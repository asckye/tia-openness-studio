# Bridge protocol

`TiaOpenness.Bridge.exe` speaks **newline-delimited JSON-RPC 2.0**:

- requests on **stdin**, one JSON object per line
- responses and `progress` notifications on **stdout**, one per line
- diagnostics on **stderr** — never on stdout, which carries protocol only

Any language can drive it. `TiaOpenness.Client` is the C# implementation; a Python or Node
client is a hundred lines.

```bash
TiaOpenness.Bridge.exe [--mock] [--openness-version 21.0] [--doctor]
```

`--doctor` prints a human-readable environment report and exits (0 = usable, 1 = not).

## Frames

```jsonc
// request
{"jsonrpc":"2.0","id":"1","method":"device.list","params":{}}

// response
{"jsonrpc":"2.0","id":"1","result":[ ... ]}

// error
{"jsonrpc":"2.0","id":"1","error":{"code":-32000,"message":"Not connected. Call session.connect first."}}

// progress notification - no id, do not wait for it
{"jsonrpc":"2.0","method":"progress","params":{"operation":"export","current":3,"total":10,"message":"Motion/FB_Axis"}}
```

## Methods

| method | params | returns |
|---|---|---|
| `ping` | — | `{pong, mode, decision}` |
| `doctor.run` | — | `DoctorReport` |
| `session.connect` | `withUserInterface`, `attachToRunning`, `version` | `SessionState` |
| `session.disconnect` | — | `SessionState` |
| `session.state` | — | `SessionState` |
| `project.open` | `path` | `ProjectInfo` |
| `project.info` | — | `ProjectInfo` |
| `project.save` | — | `{saved:true}` |
| `project.close` | — | `{closed:true}` |
| `device.list` | — | `DeviceInfo[]` |
| `block.list` | `deviceId`, `includeSystemBlocks` | `BlockInfo[]` |
| `block.export` | `deviceId`, `blocks[]`, `outputDirectory`, `format`, `preserveFolders` | `ExportResult` |
| `block.import` | `deviceId`, `files[]`, `overwrite` | `ExportResult` |
| `tag.tables` | `deviceId` | `TagTableInfo[]` |
| `tag.list` | `deviceId`, `tableName` | `TagInfo[]` |
| `compile.device` | `deviceId`, `softwareOnly` | `CompileResult` |
| `inspect.run` | `deviceId`, `blockNamePattern`, `requireBlockComment`, `findUnusedBlocks`, `flagInconsistentBlocks` | `InspectionReport` |
| `vc.supported` | — | `{supported}` |
| `vc.workspaces` | — | `WorkspaceInfo[]` |
| `vc.workspace.create` | `name`, `folderPath` | `WorkspaceInfo` |
| `vc.map` | `workspaceName`, `deviceId`, `dryRun` | `MappingResult` |
| `vc.status` | `workspaceName`, `changedOnly` | `WorkspaceStatusReport` |
| `vc.sync` | `workspaceName`, `direction`, `dryRun` | `SyncResult` |
| `openness.build` | `version` | `AdapterBuildResult` |

`openness.build` compiles the Openness adapter against the TIA Portal installed on this machine
and writes it next to the bridge. It is the one step that cannot happen before shipping, because
the Siemens assemblies it compiles against are not redistributable. The next `session.connect`
picks the result up: a failed backend resolution is retried rather than cached, so nothing has to
be restarted.

`blocks[]` empty or absent means *all blocks*. Block addresses are the slash-separated path
shown by `block.list`, e.g. `Motion/FB_Axis`; a bare block name also resolves when unambiguous.

## Error codes

| code | meaning | what the caller should do |
|---|---|---|
| `-32700` | Parse error | Fix the frame |
| `-32600` | Invalid request | `method` was missing |
| `-32601` | Method not found | The message lists the known methods |
| `-32602` | Invalid params | A required parameter was missing or malformed |
| `-32603` | Internal error | Bug; `data.stack` carries the trace |
| `-32000` | Not connected | Call `session.connect` |
| `-32001` | No project open | Call `project.open` |
| `-32002` | Openness failure | `data.type` / `data.inner` carry the Siemens exception |
| `-32003` | Environment unusable | Call `doctor.run` and act on the remedies |
| `-32004` | No Version Control Interface | TIA Portal below V21; hide the feature rather than reporting a fault |

## Talking to it by hand

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":"1","method":"session.connect","params":{}}' \
  '{"jsonrpc":"2.0","id":"2","method":"project.open","params":{"path":"D:/demo/Line.ap21"}}' \
  '{"jsonrpc":"2.0","id":"3","method":"device.list","params":{}}' \
  | TiaOpenness.Bridge.exe --mock
```

## Session and lifetime rules

- **One bridge process binds one Openness version**, decided on the first `session.connect` (or
  by `--openness-version`). The CLR caches resolved assemblies per AppDomain; switching is
  refused with a clear message rather than failing later in an obscure way.
- **Closing stdin ends the session cleanly** — the read loop exits and the TIA Portal object is
  disposed. `TiaClient.Dispose` does this, then force-kills after 5 seconds.
- **Calls are serialised.** TIA Portal is single-threaded per instance; the bridge processes one
  request at a time and emits `progress` notifications from inside the running call.
- **Long calls are normal.** `BridgeClient.DefaultTimeout` is 10 minutes, because a full-project
  export on a large program legitimately takes minutes.
