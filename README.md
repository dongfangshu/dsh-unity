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
| `cs` | `code`, `imports?`, `data?` | compile + run agent-written C# with Roslyn (in memory, no domain reload); `data` JSON becomes the script global `Args` |
| `hierarchy` | `recursive?` | list scene root objects (`name`, `path`, `active`, `children`) |
| `log` | `lines?` | tail of captured console log |

## Security note

`eval` can run arbitrary static code inside the editor (e.g.
`Debug.Break()`, `AssetDatabase.Refresh()`). The bridge binds to nothing but
the local filesystem; keep `in/` trusted.

## Roslyn C# scripting (`cs` op / `unity_cs` tool)

The embedded UPM package `UnityMain/Packages/com.dsh.roslyn` bundles
Microsoft.CodeAnalysis 3.8.0 (editor-only, `Editor/` folder) plus its runtime
deps (`System.Collections.Immutable` 1.5.0, `System.Reflection.Metadata` 1.6.0,
`System.Text.Encoding.CodePages` 4.5.1 — versions matching Unity's own
netstandard 2.1 BCL to avoid conflicts). No domain reload: scripts compile to
memory and run on the editor main thread.

- Language version: C# 9 (Roslyn 3.8). `download.js` in the package folder
  re-fetches the packages from nuget.org — bump the versions there to upgrade.
- References: every assembly currently loaded in the editor (UnityEngine,
  UnityEditor, your own scripts, ...).
- Default imports: `System`, `System.Collections.Generic`, `System.Linq`,
  `System.Text`, `System.IO`, `System.Threading`,
  `System.Text.RegularExpressions`, `UnityEngine`, `UnityEditor`.
- The script sees one global: `Args` (the JSON object passed as `data`).
- The last expression's value is returned as `result.value` (scalars pass
  through; other objects become their `ToString()`).
- Compile errors and runtime exceptions come back as `"ok": false` with
  diagnostics in `error`.

## Agent-side tools

- `unity_status` — is the bridge online? current state
- `unity_exec` — send one op and wait for the response
- `unity_cs` — compile and run agent-written C# in the editor (Roslyn)
- `unity_log` — tail of the captured Unity console log
