# Deploying on the TIA Portal V21 machine

This repository was developed on a machine **without** TIA Portal, against the mock session.
Everything below is what has to be true on the machine that has V21.

## 1. Prerequisites

| requirement | check | fix |
|---|---|---|
| TIA Portal V15.1+ with the **Openness** option | `tia doctor` → `TIA-INSTALL` | Re-run the TIA setup and tick Openness |
| .NET Framework 4.8 runtime | `tia doctor` → `ENV-NETFX` | Ships with Windows 10 1903+; otherwise install it |
| .NET 10 SDK (to build) / runtime (to run the front ends) | `dotnet --info` | https://dot.net |
| Membership of the local group `Siemens TIA Openness` | `tia doctor` → `TIA-GROUP` | See below |
| x64 process | `tia doctor` → `ENV-BITNESS` | Already enforced by the build |

### The group is the one people get wrong

```bash
net localgroup "Siemens TIA Openness" "%USERDOMAIN%\%USERNAME%" /add
```

Run it **as Administrator**, then **log off and log back on**. Group membership is baked into
the logon token; until you get a new token, Openness fails with a COM error that says nothing
about groups. `tia doctor` checks the current token, not the group's member list, so it tells
you the truth about whether the log-off actually happened.

## 2. Build

```powershell
git clone <this repo>
cd openness
powershell -ExecutionPolicy Bypass -File tools\fetch-openness-dlls.ps1
dotnet build TiaOpenness.slnx -c Release
```

`fetch-openness-dlls.ps1` copies `Siemens.Engineering.dll` from the TIA install into `lib\`.
Once that file exists, `TiaOpenness.Bridge` picks up a project reference to
`TiaOpenness.Openness` automatically and the real backend is built. Without it the solution
still builds; the bridge then refuses a real session with a message naming this step, and
`--mock` still works — which is how CI on a machine without TIA Portal stays green.

Verify which backend you got:

```bash
tia doctor
```

The first stderr line of the bridge says `mode=Openness` or `mode=Mock` and why.

## 3. First connection

Connect **once with the user interface visible**:

```bash
tia devices --project D:\projects\Line.ap21
```

TIA Portal shows a security dialog the first time an unknown application connects. Accept it.
A headless session (`--headless`, or `withUserInterface: false`) cannot display that dialog and
will appear to hang instead. After it has been accepted once, headless runs work.

## 4. Errors you will actually hit

| symptom | cause | fix |
|---|---|---|
| `Retrieving the COM class factory ... 80040154` | Not in the Openness group, or no log-off since being added | See §1 |
| Connect hangs forever, no window | Headless connect before the trust dialog was ever accepted | Connect once with the UI |
| `EngineeringTargetInvocationException` on export | Block is inconsistent | Compile the device first; the export result names the block |
| Export fails on one block only | Know-how protection | Remove protection in TIA, or exclude the block |
| `Could not load file or assembly 'Siemens.Engineering'` | The resolver bound a version that is not installed, or lib was built from a different TIA version | `tia doctor` lists what *is* installed and which layout; re-run fetch-openness-dlls.ps1 from the right version |
| `Access to a disposed object of type Workspace` | A version-control call outlived the service it came from | Fixed in the adapter by rooting the proxies; report it if it reappears |
| `Synchronize cannot be called on a workspace mapping ... equal` | Something asked TIA to sync an in-sync object | Not reachable through this tool - equal objects are skipped by design |
| Bridge exits immediately | x86 host process | The bridge is `PlatformTarget=x64`; do not override it |
| Two TIA versions needed at once | One AppDomain binds one version | Run two bridges; pass `--bridge` to each front end |

## 5. Running unattended

```bash
tia compile --project D:\p\Line.ap21 --device PLC_1 --headless
```

Exit code `1` on a compiler error, so CI fails without parsing output. Two caveats for
unattended runs:

- The trust dialog must have been accepted on that machine, under **that user account**, before.
- TIA Portal is single-threaded per instance. Run one bridge per project; do not fan out.

## 6. Version control (V21 only)

The Version Control Interface is exposed as `tia vci`, a tab in the desktop app, and six MCP
tools. Verify it early, because it is the one feature that silently does not exist on V20:

```bash
tia vci workspaces --project D:\projects\Line.ap21
```

An older TIA answers with a clear message rather than an error, and the desktop tab disables
itself. See [version-control.md](version-control.md) for the full loop and the API rules it has
to work around.

## 7. What is not covered yet

- HMI / WinCC Unified objects (`Siemens.Engineering.Hmi.dll` / `.WinCC.dll` are fetched but unused).
- Hardware configuration in version control — VCI covers the software side only.
- A TIA Portal add-in (right-click menu inside TIA). `TiaOpenness.AddIn` is reserved for it.
