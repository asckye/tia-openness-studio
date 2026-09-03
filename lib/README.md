# lib/

This folder holds the Siemens Openness assemblies used **at compile time only**.

They are not in version control: Siemens ships `Siemens.Engineering.dll` inside the TIA Portal
installation and does not redistribute it.

## Fill it

On a machine with TIA Portal installed:

```powershell
.\tools\fetch-openness-dlls.ps1          # newest installed version
.\tools\fetch-openness-dlls.ps1 -Version 21.0
```

That copies:

| file | required | used for |
|---|---|---|
| `Siemens.Engineering.dll` | yes | the whole PLC-side API |
| `Siemens.Engineering.Hmi.dll` | no | HMI/WinCC objects (not used yet) |
| `Siemens.Engineering.AddIn.dll` | no | the TIA Portal add-in (planned) |

## Why compile-time only

`TiaOpenness.Openness.csproj` references them with `<Private>false</Private>`, so they are
**not** copied to the build output. At run time `OpennessAssemblyResolver` loads them from the
TIA Portal install directory instead. A stale copy sitting next to the bridge would shadow the
correct one and produce version-mismatch errors that are hard to trace.

Once `Siemens.Engineering.dll` is present here, `dotnet build` picks the Openness adapter up
automatically. Without it the whole solution still builds, but the bridge refuses a real
session and says why; `--mock` is then the only backend.
