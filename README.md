# DSH ↔ Unity Bridge (v2)

File-queue bridge that lets the DSH agent control the Unity editor from
`D:\DSH Unity\UnityMain`. No network ports — commands and responses travel as
JSON files under `Library/UnityBridge/` inside the Unity project (machine-local,
never version-controlled, auto-recreated on init).

## Components

| Side | File | Role |
|---|---|---|
| Unity | `UnityMain/Assets/Plugins/UnityBridge/` (UPM package: `Editor/` asmdef `DSH.UnityBridge.Editor` + `CoreHandler/SceneHandler/AssetHandler/ScriptHandler.cs` + bundled Roslyn DLLs flat in `Editor/` + `Runtime/` + `Samples~`) | Polls `in/`, routes by domain, executes ops on the main thread, writes responses, heartbeat + log capture |
| DSH | dynamic Cordis plugin (`unity_status`, `unity_exec`, `unity_cs`, `unity_log` tools) | Writes command files, polls `out/`, reports results |

The bridge auto-starts when the editor loads. Toggle via
`Tools > Unity Bridge > Enable/Disable`.

## Folder layout (`<project>/Library/UnityBridge/`)

The runtime queue lives under the Unity project's `Library/` folder — the same
place Unity keeps its own cache, so it is machine-local, never committed, and
safe to wipe (the bridge recreates it on init):

- `in/` — command files `<id>.json`, written by the agent
- `out/` — response files `<id>.json`, written by Unity (pruned after 120s)
- `done/` — processed commands moved here (pruned after 600s)
- `status/heartbeat.json` — refreshed every 1s; agent uses it to detect "online"
- `status/log.json` — last 300 captured console lines

## Protocol (v2)

Ops are namespaced by **domain** (`core | scene | asset | script`); each domain
is handled by its own `*Handler.cs` file in the package. Command envelope:

```json
{ "id": "m1abc-xyzw", "domain": "scene", "op": "open", "args": { "path": "Assets/Scenes/SampleScene.unity" } }
```

Response envelope:

```json
{ "id": "m1abc-xyzw", "domain": "scene", "op": "open", "ok": true, "ts": 1699999999.123, "result": { "scene": "Assets/Scenes/SampleScene.unity", "loaded": true } }
```

Errors: `"ok": false, "error": "..."`.

## Ops

| domain | op | args | effect |
|---|---|---|---|
| `core` | `ping` | — | round-trip check (`result.pong` = true) |
| `core` | `status` | — | fresh snapshot: play mode, paused, scene, roots, selection, version |
| `core` | `log` | `lines?` | tail of captured console log (max 300) |
| `core` | `reload` | — | recompile all scripts + domain reload **in place** (no editor restart); heartbeat pauses then resumes |
| `core` | `menu` | `item` | `EditorApplication.ExecuteMenuItem` (exact path) |
| `scene` | `open` | `path`, `additive?` | open scene (single or additive) |
| `scene` | `save` | — | save open scenes |
| `scene` | `play` / `stop` | — | enter / exit play mode |
| `scene` | `pause` / `resume` / `step` | — | play-mode stepping |
| `scene` | `hierarchy` | `recursive?` | list scene root objects (`name`, `path`, `active`, `children`) |
| `asset` | `refresh` | — | `AssetDatabase.Refresh()` |
| `asset` | `import` | `path` | import one asset by project path, e.g. `"Assets/Foo.png"` |
| `asset` | `list` | `path?`, `max?` | list asset paths under a folder (default `Assets`, capped) |
| `script` | `eval` | `type`, `method`, `argsJson?` | invoke any static method; `argsJson` is a JSON array of scalars |
| `script` | `cs` | `code`, `imports?`, `data?` | compile + run agent-written C# with Roslyn (in memory, no domain reload); code must define `Entry.Main(object args)`; `data` JSON → `args` |

Unknown domains/ops are rejected with an error response. Adding a new domain
= one handler class + one `case` in `UnityBridge.Execute`.

## Security note

`script.eval` / `script.cs` can run arbitrary code inside the editor (e.g.
`Debug.Break()`, `AssetDatabase.Refresh()`). The bridge binds to nothing but
the local filesystem; keep `in/` trusted.

## Roslyn C# scripting (`script.cs` op / `unity_cs` tool)

The UPM package at `Assets/Plugins/UnityBridge/` bundles the bridge plus
Microsoft.CodeAnalysis 3.8.0 (editor-only, DLLs flat in `Editor/`, referenced
by the `DSH.UnityBridge.Editor` asmdef) with its exact runtime deps at the
assembly versions Roslyn references (`System.Collections.Immutable` 5.0.0,
`System.Reflection.Metadata` 5.0.0, `System.Memory` 4.0.1.1,
`System.Threading.Tasks.Extensions` 4.2.0.1, `System.Runtime.CompilerServices.Unsafe`
4.0.6.0, `System.Text.Encoding.CodePages` 4.1.1.0, `System.Buffers` 4.0.3.0,
`System.Numerics.Vectors` 4.1.4.0). No domain reload: scripts compile to
memory and run on the editor main thread.

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
- `unity_exec` — send one op (any domain) and wait for the response
- `unity_cs` — compile and run agent-written C# in the editor (Roslyn)
- `unity_log` — tail of the captured Unity console log

The plugin is **host-only (no web settings UI)**. It finds the Unity project
automatically under the workspace root or `<workspace>/UnityMain` (probed on
every call) — the user is expected to have the editor open, and no path
configuration is needed.

> **Agent-agnostic**: the bridge speaks a plain file-queue protocol, so *any*
> agent with file access can drive Unity. `skills/unity-bridge/SKILL.md` is a
> ready-made Agent Skill (Anthropic format). It is **loaded on demand**: copy
> or mount it into an agent only when the user asks to control Unity — do not
> auto-inject it into every agent's context. The skill never locates the
> bridge by a fixed path and never launches Unity; it discovers the project
> under the current workspace and reports offline to the user.

> The dynamic plugin itself is process-local: after a DSH restart it must be
> re-created and re-approved, but the Unity side (and its config-free
> auto-detection) survives.
