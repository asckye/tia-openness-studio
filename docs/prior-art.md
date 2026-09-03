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
