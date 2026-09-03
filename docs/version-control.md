# Putting a TIA project under Git

A TIA project file is binary, so Git can store it but cannot diff, merge or review it. That is
why "version control" in this field has so often meant a folder of dated *Save As* copies.

TIA Portal V21's **Version Control Interface** (VCI) fixes it. A *workspace* is an ordinary
folder holding one text file per mapped object. Point it at a Git working tree and the project
becomes reviewable.

## Two ways to get text out, and when to use which

|  | `tia export --format Source` | `tia vci` (V21+) |
|---|---|---|
| TIA version | V15.1+ | **V21+ only** |
| Must compile first | **yes** — TIA refuses to export an inconsistent block | no, for change *detection* |
| Granularity | per block, one file each | per mapped object |
| Knows what changed | no — diff the files afterwards | **yes** — per-object compare state |
| Restore back into the project | manual import | `pull`, one call |
| Know-how protected blocks | fail | reported as unsupported |

Use VCI when you have V21. Use `export --format Source` when you do not, or when you want a
plain snapshot without adding configuration to the project.

## The loop

```bash
tia vci create --project D:\p\Line.ap21 --name git --folder D:\repo\plc
```

```bash
tia vci map --project D:\p\Line.ap21 --apply
```

```bash
tia vci push --project D:\p\Line.ap21 --apply
```

```bash
git -C D:\repo\plc add -A && git -C D:\repo\plc commit -m "PLC_1 snapshot"
```

Later, to see what an engineer changed:

```bash
tia vci status --project D:\p\Line.ap21
```

```
Unequal               PLC_1/Motion/FB_Axis
Unequal               PLC_1/Safety/FC_EStop

Workspace 'git' (D:\repo\plc)
11 mapped object(s), 2 differ. Write them out with:  tia vci push --apply
```

To bring a reviewed version back into the project after a `git pull`:

```bash
tia vci pull --project D:\p\Line.ap21 --apply
```

Then compile and save — `pull` leaves the restored blocks inconsistent, exactly as the TIA UI
would.

## Safety

Every mutating sub-command is a **dry run unless you pass `--apply`**. The desktop app has the
same gate as a "Dry run" checkbox, ticked by default.

- `map` and `create` write configuration **into the TIA project**.
- `pull` **overwrites blocks in the open project** and cannot be undone.
- `push` only writes files on disk; the project is untouched.

In the MCP server the split is by tier: `tia_vc_workspaces`, `tia_vc_status` and `tia_vc_push`
are always available; `tia_vc_create_workspace`, `tia_vc_map` and `tia_vc_pull` appear only with
`--allow-write`.

## CI

`tia vci status` exits **1** when anything differs, so a pipeline can fail when someone changed
the project without committing:

```bash
tia vci status --project D:\p\Line.ap21 --headless
```

## Things the API enforces, that surprised us

These are TIA's rules, discovered by running against V21 — the code works with them rather than
around them:

- **An object already in sync cannot be synchronized.** TIA answers "Synchronize cannot be
  called on a workspace mapping that has a compare status of equal". So "force everything" is
  not an option that exists; equal objects are always skipped and counted separately.
- **Mapping is `ExportObject`, not `ConnectObject`.** Despite the names, `ExportObject` writes
  the file *and* creates the mapping. `ConnectObject` only binds to files that already exist.
- **Sub-folders inside a workspace can be refused** ("Relative Directory Path is Invalid") on
  some builds. The mapper tries a sub-folder first and falls back to a flat layout at the
  workspace root, folding the project path into the file name so nothing collides.
- **Openness proxies die with their parent.** The VCI service and every group reached through it
  must stay referenced for as long as the project is open, or objects obtained earlier throw
  "Access to a disposed object of type Workspace". The adapter roots them deliberately and
  re-acquires after any throw.
- **Not everything is mappable.** Know-how protected blocks and hardware configuration are
  outside VCI. They are reported as `unsupported` rather than skipped silently.

## Line endings

Add a `.gitattributes` to the workspace repository so exported text does not churn between
CRLF and LF on different machines:

```gitattributes
*.s7dcl text eol=crlf
*.xml   text eol=crlf
```

## Trying it without TIA Portal

The mock backend implements the whole loop — workspaces point at real folders, mapping writes
real files, and status really compares content — so the Git workflow can be rehearsed anywhere:

```bash
tia vci create --mock --project D:\demo\Line.ap21 --name git --folder D:\repo\demo
```

```bash
tia vci map --mock --project D:\demo\Line.ap21 --apply
```

Mock workspaces persist per project path under
`%LOCALAPPDATA%\TiaOpenness\mock-vci`, because real VCI configuration lives inside the project
and survives reopening it. Delete that folder to start over.
