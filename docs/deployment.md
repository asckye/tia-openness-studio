# Deploying on the TIA Portal V21 machine

This repository was developed on a machine **without** TIA Portal, against the mock session.
Everything below is what has to be true on the machine that has V21.

## 1. Prerequisites

| requirement | check | fix |
|---|---|---|
| TIA Portal V15.1+ with the **Openness** option | **Doctor** → `TIA-INSTALL` | Re-run the TIA setup and tick Openness |
| .NET Framework 4.8 runtime | **Doctor** → `ENV-NETFX` | Ships with Windows 10 1903+; otherwise install it |
| .NET 10 SDK — **only if building from source** | `dotnet --info` | https://dot.net. A release package needs none: the front ends are self-contained and the adapter is built by the bundled compiler |
| Membership of the local group `Siemens TIA Openness` | **Doctor** → `TIA-GROUP` | See below |
| x64 process | **Doctor** → `ENV-BITNESS` | Already enforced by the build |

### The group is the one people get wrong

```bash
net localgroup "Siemens TIA Openness" "%USERDOMAIN%\%USERNAME%" /add
```

Run it **as Administrator**, then **log off and log back on**. Group membership is baked into
the logon token; until you get a new token, Openness fails with a COM error that says nothing
about groups. Doctor checks the current token, not the group's member list, so it tells
you the truth about whether the log-off actually happened.

## 2. Get the binaries

Two routes. Both end with the same thing: an Openness adapter compiled against *your* TIA
installation, because that adapter cannot be prebuilt by anyone else.

### A. From a release — recommended, no SDK

Download `TiaOpenness-v<version>-win-x64.exe` from the
[latest release](https://github.com/asckye/tia-openness-studio/releases/latest). That is the whole
product: nothing to unzip, nothing to install. Run it and press **Enable Openness** in the header,
once.

It finds your TIA installation, compiles the adapter sources carried inside the executable against
its assemblies using the bundled C# compiler, and writes the result next to the unpacked bridge in
`%LOCALAPPDATA%\TiaOpenness\<version>\bridge\`. The next **Connect** uses it — no restart.

If it reports compile errors, each one names a file and line in the adapter sources. That means
this build expects an Openness API your TIA version does not have; the errors are the bug report,
so please open an issue with them.

Rebuild after upgrading TIA Portal by pressing the button again. A new release unpacks into its
own version folder, so upgrading the app never reuses an adapter built for a different one.

### B. From source

```powershell
git clone https://github.com/asckye/tia-openness-studio
cd tia-openness-studio
powershell -ExecutionPolicy Bypass -File tools\fetch-openness-dlls.ps1
dotnet build TiaOpenness.slnx -c Release
```

`fetch-openness-dlls.ps1` copies `Siemens.Engineering.dll` from the TIA install into `lib\`.
Once that file exists, `TiaOpenness.Bridge` picks up a project reference to
`TiaOpenness.Openness` automatically and the real backend is built. Without it the solution
still builds; the bridge then refuses a real session with a message naming this step, and
`--mock` still works — which is how CI on a machine without TIA Portal stays green.

Verify which backend you got by pressing **Doctor**. The bridge's first log line says
`mode=Openness` or `mode=Mock` and why.

## 3. First connection

Press **Connect** once **with the TIA window visible** — leave *Headless* unticked.

TIA Portal shows a security dialog the first time an unknown application connects. Accept it.
A headless session cannot display that dialog and will appear to hang instead. After it has been
accepted once on that machine under that user account, headless runs work.

## 4. Errors you will actually hit

| symptom | cause | fix |
|---|---|---|
| `Retrieving the COM class factory ... 80040154` | Not in the Openness group, or no log-off since being added | See §1 |
| Connect hangs forever, no window | Headless connect before the trust dialog was ever accepted | Connect once with the UI |
| `TiaOpenness.Openness.dll is not deployed next to the bridge` | The adapter has not been built on this machine | Press **Enable Openness** |
| `EngineeringTargetInvocationException` on export | Block is inconsistent | Compile the device first; the export result names the block |
| Export fails on one block only | Know-how protection | Remove protection in TIA, or exclude the block |
| `Could not load file or assembly 'Siemens.Engineering'` | The adapter was built against a version that is no longer installed | Doctor lists what *is* installed and which layout; press **Enable Openness** again |
| `Access to a disposed object of type Workspace` | A version-control call outlived the service it came from | Fixed in the adapter by rooting the proxies; report it if it reappears |
| `Synchronize cannot be called on a workspace mapping ... equal` | Something asked TIA to sync an in-sync object | Not reachable through this tool — equal objects are skipped by design |
| Bridge exits immediately | x86 host process | The bridge is `PlatformTarget=x64`; do not override it |

## 5. Running unattended

The MCP server is the unattended surface:

```bash
TiaOpenness.exe mcp --allow-write
```

Two caveats:

- The trust dialog must have been accepted on that machine, under **that user account**, before.
- TIA Portal is single-threaded per instance. Run one bridge per project; do not fan out.

## 6. Version control (V21 only)

The Version Control Interface is the *Version control* tab, plus six MCP tools. Check it early,
because it is the one feature that silently does not exist on V20: on an older TIA the tab
disables itself and says so, rather than failing when a button is pressed.

See [version-control.md](version-control.md) for the full loop and the API rules it has to work
around.

## 7. What is not covered yet

- HMI / WinCC Unified objects (`Siemens.Engineering.Hmi.dll` / `.WinCC.dll` are fetched but unused).
- Hardware configuration in version control — VCI covers the software side only.
- A TIA Portal add-in (right-click menu inside TIA). `TiaOpenness.AddIn` is reserved for it.
