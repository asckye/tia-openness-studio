# Prior art and licence position

What was surveyed on GitHub before building, what was taken, and what could not be taken.

## Reviewed

| project | stars | licence | verdict |
|---|---|---|---|
| [bulaofen0036-coder/TIA_Portal_Openness_MCP](https://github.com/bulaofen0036-coder/TIA_Portal_Openness_MCP) | 211 | **MIT** | Closest in scope: V20/V21, MCP + CLI, prebuilt runtime, V21 Version Control Interface. MIT allows reuse with attribution. Its layering (one binary serving both MCP and a declarative CLI) informed the front-end split here. |
| [siemens/tia-portal-openness-code-snippets](https://github.com/siemens/tia-portal-openness-code-snippets) | 63 | Siemens royalty-free, sharing-platform terms | Siemens' own V20 samples. Used as the reference for *which* API call is correct, not copied. Read the licence before vendoring any of it. |
| [tia-portal-applications/tia-portal-openness-unified-library](https://github.com/tia-portal-applications/tia-portal-openness-unified-library) | 13 | not declared | Ships a DLL, no source. Its `UnifiedOpennessConnector` disposable-session shape matches `ITiaSession` here. Nothing taken — an undeclared licence means no rights are granted. |
| [Parozzz/TiaUtilities](https://github.com/Parozzz/TiaUtilities) | 37 | **GPL-3.0** | Best SimaticML XML generation/parsing available, including LAD. **Copying any of it would make this whole product GPL-3.0.** Concepts only; no code. |
| [mking2203/CodeGeneratorOpenness](https://github.com/mking2203/CodeGeneratorOpenness) | 165 | — | Stopped in 2023 at the V16 API. Historical interest only. |
| [Repsay/tia-openness-api-client](https://github.com/Repsay/tia-openness-api-client) | 51 | — | Python over pythonnet. Relevant only if a Python front end is added; the bridge protocol here is language-neutral, so a Python client is ~100 lines. |
| [StaniB88/AnyAutomationStudio](https://github.com/StaniB88/AnyAutomationStudio) | 8 | closed, commercial | Competitor, not a source. Useful as a feature checklist: SCL unit tests on PLCSIM Advanced, OPC UA, multi-version V15–V21. |

## Second survey, September 2026

Re-run once this project had a working V21 adapter, looking specifically at the gaps in
[roadmap.md](roadmap.md). Three of these are new and matter.

| project | stars | licence | verdict |
|---|---|---|---|
| [Czarnak/tia-git-addin](https://github.com/Czarnak/tia-git-addin) | 11 | **MIT** | **The most valuable find.** A V21 Add-In that puts a Git panel inside TIA Portal, aware of VCI workspaces. It carries what this project does not: a native C# SimaticML parser and a **visual LAD diff** that renders old and new networks side by side. Directly complementary — this project gets the text out to Git, that one reviews it. MIT, so reusable with attribution. |
| [EidoAut/EidoTiaWorkbench](https://github.com/EidoAut/EidoTiaWorkbench) | 0 | **MIT** | Covers the HMI gap: classic WinCC screens, templates, popups, slide-ins, HMI tag tables, connections, text lists, graphic lists, user cycles, VB scripts. V21-only, `Siemens.Engineering.WinCC.dll`, `Private=False` — the same assembly discipline used here. Worth reading before any HMI work. |
| [Sawascwoolf/BlockParam](https://github.com/Sawascwoolf/BlockParam) | 5 | MIT, with a paid tier | Bulk editing of Data Block start values across UDT instances, with type validation and a diff preview before writing — the "batch modification" item on the roadmap, done. Note the split licence: read it before taking anything. |
| [eliasrhoden/TiaTools](https://github.com/eliasrhoden/TiaTools) | 2 | MIT | Small Add-Ins. Useful only as an example of Add-In packaging. |

### The finding worth keeping regardless of the code

`tia-git-addin` published a [V21 compare API investigation](https://github.com/Czarnak/tia-git-addin/blob/main/docs/tia-v21-compare-api-investigation.md)
done against Public API build `2100.0.121.1`, by reading the installed XML documentation and
assembly metadata rather than by guessing. Its conclusion saves anyone else the same search:

- V21 **does** expose comparison APIs, but they are *data* APIs over TIA engineering objects.
  They return hierarchical result objects.
- They do **not** accept VCI workspace files, SimaticML files, Git commit ids, paths, streams or
  raw revision content — so they cannot compare two revisions of a block.
- The graphical LAD/FBD comparison editor lives in `Siemens.Automation.CommonServices.Compare.*`,
  which is **internal** and not part of the Public API or the Add-In contract. Using it is
  outside the supported surface.

So block diffing has to be built on your own SimaticML parser, exactly as they concluded.

### Still nothing found for

- Generating tag tables or data blocks from a spreadsheet. Every project that touches this writes
  its own SimaticML by hand.
- Running SCL against PLCSIM Advanced from open source. Only `AnyAutomationStudio` claims it, and
  it is closed.
- Siemens' own snippets are **still V20**, not V21, as of this survey.

## What this repository actually reuses

Nothing is vendored. The reuse is at the level of technique, all of it publicly documented by
Siemens or independently rediscoverable:

- **Registry layout** for locating `Siemens.Engineering.dll`
  (`HKLM\SOFTWARE\Siemens\Automation\Openness\<ver>\PublicAPI\<ver>`), with a filesystem probe
  as fallback — `OpennessLocator`.
- **`AssemblyResolve` + NoInlining boundary**, the standard fix for the fact that the JIT
  resolves an assembly when it compiles a method, not when it runs — `OpennessAssemblyResolver`,
  `OpennessSessionFactory`.
- **`Siemens TIA Openness` group membership checked against the access token** rather than the
  group's member list, so "added but never logged off" is reported correctly — `OpennessDoctor`.
- **Export/import routes**: `PlcBlock.Export` for SimaticML, `ExternalSourceGroup.GenerateSource`
  for text, `ExternalSources.CreateFromFile` + `GenerateBlocksFromSource` for text import.

## Licence position of this repository

No third-party source is included, so there is no inherited copyleft. `lib/` is empty in version
control by design: the Siemens assemblies are not redistributable and are fetched from a local
TIA Portal installation at build time.

If GPL-3.0 code from `TiaUtilities` is ever pulled in — its SimaticML LAD generation is the most
likely temptation — the whole product becomes GPL-3.0. Isolate it in a separate process behind
the existing JSON-RPC boundary, or reimplement it.
