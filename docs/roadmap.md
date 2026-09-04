# Status and roadmap

Against the four problem areas that were prioritised at the start.

## Done and verified on the mock backend

| area | what works |
|---|---|
| **Version control (V21 VCI)** | Create a workspace over a Git working tree, map the whole project in one call, per-object compare state, push (project → text) and pull (text → project). Mutating operations are a dry run unless explicitly applied. `vci status` exits 1 on drift so CI can fail on uncommitted project edits. Verified end to end against the mock, including a real `git commit` and a round trip through an external edit. |
| **Batch export / import** | Export all or selected blocks to SimaticML `.xml` or text `.scl`/`.db`/`.udt`, mirroring the TIA folder structure; per-block success/failure with the real reason. Import `.xml` and text sources with an explicit overwrite flag. |
| **Project inspection** | `InspectionEngine` with five rules — naming convention (regex), missing block author, inconsistent blocks, know-how protection, unreferenced blocks. Shared by both backends so the mock and the real session give identical verdicts for identical metadata. |
| **Compile diagnostics** | Compile a device, structured messages with severity/target/error code, exit code 1 on error so CI fails without parsing text. |
| **Environment** | `OpennessDoctor` — seven checks, each with the exact command that fixes it. Checks group membership against the *access token*, so "added but never logged off" is reported correctly, and reports whether the installation is the V21 modular layout or the older monolithic one. |
| **Front ends** | One executable: the desktop app (Blocks and Version control tabs) and, as `TiaOpenness.exe mcp`, the MCP server. Both over the same bridge, which the exe unpacks from inside itself. MCP hides the five mutating tools unless started with `--allow-write`. |
| **Deployment** | A single self-contained exe. The bridge, a C# compiler and the Openness adapter's sources ride inside it, so **Enable Openness** builds the real backend on the target machine with no .NET SDK, no unzipping and no restart. |

## Written but not yet executed against TIA Portal

`TiaOpenness.Openness` — the real `Siemens.Engineering` adapter, including
`OpennessVersionControl` — compiles only where `lib\` holds the Siemens assemblies, which is not
this machine. It has not been run.

Where it is most likely to need adjustment on first contact with V21:

- **Optional properties.** `HeaderAuthor`, `HeaderVersion`, `ErrorCode`, `InstanceOfName` are
  read through `Attr<T>` / `Prop<T>`, which fall back instead of throwing when a name differs
  between versions. Wrong names degrade to `null`, they do not crash — but they should be
  verified and then read directly.
- **`GenerateSource` overloads.** The `PlcType` overload and the `GenerateOptions` values are
  the parts most likely to differ.
- **Device article number / firmware.** Read as the `OrderNumber` and `FirmwareVersion`
  attributes off the device item that carries the software; some device families put them
  elsewhere.
- **VCI mapping granularity.** The walk is coarse-first: if `GetSupportedFileFormats` accepts a
  device or a PLC as a unit, its children are not visited. Whether V21 actually accepts those
  coarse objects decides how many files a mapped project produces, and only a real run answers it.
- **VCI sub-folders.** Some builds reject a relative directory. The mapper tries a sub-folder and
  falls back to a flat root layout; which path is taken on your build is worth checking once.

First run on the V21 machine should be, in order: **Enable Openness** → **Doctor** → **Connect**
→ open a project → export one block → **Compile** → the *Version control* tab → **Map project**
with *Dry run* still ticked.

## Not started

| item | note |
|---|---|
| **Batch code generation from Excel** | Tag tables and data blocks from a spreadsheet. Needs a source-text generator; the import path it would feed already exists. |
| **Batch modification** | Rename, re-comment, renumber across many blocks. Openness allows it; the risk is that it is destructive, so it needs the same dry-run gate the VCI commands have. |
| **Automated testing** | Compile-only regression works today. Running logic against PLCSIM Advanced is a separate API and a separate install. |
| **HMI beyond classic WinCC** | Classic screens, templates, popups, slide-ins and HMI tag tables list and export. WinCC Unified is a different object model and is not covered. |
| **Block-level diff beyond text** | The workspace diff is Git's, over the exported text. A semantic or LAD diff needs an own SimaticML parser: V21's compare API is a data API that accepts neither files nor revisions, and its graphical comparer is internal. See [prior-art.md](prior-art.md). |
| **TIA Portal add-in** | `src/TiaOpenness.AddIn` is reserved. An add-in runs inside TIA, so it would reference `TiaOpenness.Core` directly and skip the bridge entirely. |
| **Multi-version in one UI** | The protocol supports it (one bridge per version); the front ends currently start exactly one. |

## Known limitations that are TIA's, not this tool's

- A block must compile before it can be **exported**. VCI change *detection* does not need it.
- Know-how protected blocks cannot be exported or version-controlled at all.
- Hardware configuration is outside VCI.
- An object already in sync cannot be synchronized; TIA refuses.
- The first connection from an unknown application shows a trust dialog that a headless session
  cannot display.
- One TIA Portal instance is single-threaded; parallel calls do not speed anything up.
- V21 broke binary compatibility with V20 and earlier. One build targets one of the two layouts.
