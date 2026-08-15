# DSH ↔ Unity Bridge (v1)

File-queue bridge that lets the DSH agent control the Unity editor from
`D:\DSH Unity\UnityMain`. No network ports — commands and responses travel as
JSON files under `UnityMain/UnityBridge/`.

## Components

| Side | File | Role |
|---|---|---|
| Unity | `UnityMain/Assets/Editor/UnityBridge.cs` | Polls `in/`, executes ops on the main thread, writes responses, heartbeat + log capture |
| DSH | dynamic Cordis plugin (`unity_status`, `unity_exec`, `unity_log` tools) | Writes command files, polls `out/`, reports results |

The bridge auto-starts when the editor loads. Toggle via
`Tools > Unity Bridge > Enable/Disable`.

## Folder layout (`UnityMain/UnityBridge/`)

- `in/` — command files `<id>.json`, written by the agent
- `out/` — response files `<id>.json`, written by Unity (pruned after 120s)
- `done/` — processed commands moved here (pruned after 600s)
- `status/heartbeat.json` — refreshed every 1s; agent uses it to detect "online"
- `status/log.json` — last 300 captured console lines

## Protocol

Command envelope:

```json
{ "id": "m1abc-xyzw", "op": "open_scene", "args": { "path": "Assets/Scenes/SampleScene.unity" } }
```

Response envelope:

```json
{ "id": "m1abc-xyzw", "op": "open_scene", "ok": true, "ts": 1699999999.123, "result": { "scene": "Assets/Scenes/SampleScene.unity", "loaded": true } }
```

Errors: `"ok": false, "error": "..."`.

## Ops

| op | args | effect |
|---|---|---|
| `ping` | — | round-trip check |
| `status` | — | fresh snapshot: play mode, paused, scene, roots, selection, version |
| `open_scene` | `path`, `additive?` | open scene (single or additive) |
| `save` | — | save open scenes |
| `play` / `stop` | — | enter / exit play mode |
| `pause` / `resume` / `step` | — | play-mode stepping |
| `menu` | `item` | `EditorApplication.ExecuteMenuItem` (exact path) |
| `eval` | `type`, `method`, `argsJson?` | invoke any static method; `argsJson` is a JSON array of scalars |
| `cs` | `code`, `imports?`, `data?` | compile + run agent-written C# with Roslyn (in memory, no domain reload); code must define `Entry.Main(object args)`; `data` JSON → `args` |
| `reload` | — | recompile all scripts + domain reload **in place** (no editor restart); heartbeat pauses then resumes |
| `hierarchy` | `recursive?` | list scene root objects (`name`, `path`, `active`, `children`) |
| `log` | `lines?` | tail of captured console log |

## Security note

`eval` can run arbitrary static code inside the editor (e.g.
`Debug.Break()`, `AssetDatabase.Refresh()`). The bridge binds to nothing but
the local filesystem; keep `in/` trusted.

## Roslyn C# scripting (`cs` op / `unity_cs` tool)

The embedded UPM package `UnityMain/Packages/com.dsh.roslyn` bundles
Microsoft.CodeAnalysis 3.8.0 (editor-only, `Editor/` folder) plus its exact
runtime deps at the assembly versions Roslyn references (`System.Collections.
Immutable` 5.0.0, `System.Reflection.Metadata` 5.0.0, `System.Memory` 4.0.1.1,
`System.Threading.Tasks.Extensions` 4.2.0.1, `System.Runtime.CompilerServices.
Unsafe` 4.0.6.0, `System.Text.Encoding.CodePages` 4.1.1.0, `System.Buffers`
4.0.3.0, `System.Numerics.Vectors` 4.1.4.0). No domain reload: scripts compile
to memory and run on the editor main thread.

- **Contract**: the code must define a static class named `Entry` with a
  `public static object Main(object args)` method. `args` is the parsed
  `data` JSON (cast to `Dictionary<string,object>` to read keys); the return
  value becomes `result.value` (scalars pass through, other objects become
  their `ToString()`).
- **Implementation note**: uses `CSharpCompilation` → emit to memory →
  `Assembly.Load(byte[])` → reflection. NOT the Roslyn Scripting API — its
  assembly loader goes through `AssemblyLoadContext`, which Unity's Mono
  stubs out with `NotImplementedException`.
- Language version: C# 9 (Roslyn 3.8). `download.js` in the package folder
  re-fetches the packages from nuget.org — bump the versions there to upgrade.
- References: every assembly currently loaded in the editor (UnityEngine,
  UnityEditor, your own scripts, ...).
- Auto-prepended `using` directives: `System`, `System.Collections.Generic`,
  `System.Linq`, `System.Text`, `System.IO`, `System.Threading`,
  `System.Text.RegularExpressions`, `UnityEngine`, `UnityEditor`; pass extra
  namespaces via `imports` (comma-separated).
- Compile errors and runtime exceptions come back as `"ok": false` with
  diagnostics in `error`.

## Agent-side tools

- `unity_status` — is the bridge online? current state
- `unity_exec` — send one op and wait for the response
- `unity_cs` — compile and run agent-written C# in the editor (Roslyn)
- `unity_log` — tail of the captured Unity console log

## Web settings (Unity paths)

The DSH plugin registers a **Unity Bridge** page in the web app's Settings
(设置 → Unity Bridge). It persists two machine-local values to
`.unity-bridge-config.json` in the workspace root (gitignored):

- `unityProjectPath` — the Unity project folder; the bridge root is derived
  as `<project>/UnityBridge` (default `D:/DSH Unity/UnityMain`)
- `unityExePath` — path to Unity.exe (default
  `C:/Unity/2022.3.4f1/Editor/Unity.exe`)

The agent-side tools read these values on every call, so a save in Settings
takes effect immediately — no plugin restart needed.

> The dynamic plugin itself is process-local: after a DSH restart it must be
> re-created and re-approved, but the config file (and Unity side) survive.
