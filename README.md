# DSH ↔ Unity Bridge (v3)

File-queue bridge that lets an agent control the Unity editor from
`D:\DSH Unity\UnityMain`. No network ports — commands and responses travel as
JSON files under `Library/UnityBridge/` inside the Unity project (machine-local,
never version-controlled, auto-recreated on init).

## Capability boundary

The bridge exposes exactly four capability domains — **read / execute / log /
core**. Nothing else is added on the op surface: new capabilities are expressed
through `execute.cs` scripts or new `read` schemes, never new ops. See
`CONTEXT.md` for the domain glossary.

- **read** — the *only* read interface; addresses anything in the editor by
  source scheme (`assets:` / `hierarchy:` / `select:`) and returns one unified
  node envelope.
- **execute** — the *only* write path; every create/update/delete is a Roslyn
  C# script compiled and run in memory.
- **log** — read the editor's captured console ring.
- **core** — a closed set of editor-session operations (menu/toolbar-level,
  object-type-independent).

## Components

| Side | File | Role |
|---|---|---|
| Unity | `UnityMain/Assets/Plugins/UnityBridge/` (UPM package: `Editor/` asmdef `DSH.UnityBridge.Editor` + `ReadHandler/ExecuteHandler/LogHandler/CoreHandler.cs` + bundled Roslyn DLLs flat in `Editor/` + `Runtime/`) | Polls `in/`, routes by domain, executes ops on the main thread, writes responses, heartbeat + log capture |
| Agent | `skills/unity-bridge/SKILL.md` (Agent Skill, Anthropic format) | Teaches the protocol so any agent with file access can drive Unity |

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

## Protocol (v3)

Ops are namespaced by **domain** (`read | execute | log | core`); each domain
is handled by its own `*Handler.cs` file in the package. Command envelope:

```json
{ "id": "m1abc-xyzw", "domain": "read", "op": "hierarchy", "args": { "path": "Assets/Scenes/SampleScene.unity/Player" } }
```

Response envelope:

```json
{ "id": "m1abc-xyzw", "domain": "read", "op": "hierarchy", "ok": true, "ts": 1699999999.123, "result": { "path": "...", "kind": "gameObject", ... } }
```

Errors: `"ok": false, "error": "..."`.

Unknown domains/ops are rejected with an error response.

## Ops

### read — the single read interface

Addresses are `scheme:address`; the engine resolves the address to an editor
object, identifies what kind it is, and returns **one node envelope**:

```
{ "path", "kind", "name"?, "type"?, "instance"?, "activeSelf"?,
  "components"?[], "children"?[], "properties"?{}, "content"? }
```

`kind ∈ { scene, gameObject, component, text, asset }`. The envelope `path` is
always a canonical address that can be read back verbatim.

| op | address | returns |
|---|---|---|
| `read.assets` | `assets:<project-relative-path>` (`Assets/...` or `Packages/...`) | text assets → `kind:"text"` with `content` (raw file); serialized assets (`.prefab`/`.asset`/`.unity`) → `kind:"asset"` with `properties` (SerializedObject dump; a `.unity` SceneAsset is near-empty by design); binary files are rejected with a pointer to `execute.cs` |
| `read.hierarchy` | `hierarchy:<scene-path>/<name>/<name>[@instance][/Type.Name]` | scene address → `kind:"scene"` with root objects as `children`; object address → `kind:"gameObject"` (ls-style: one level of `children`, `components` = type names); trailing type segment → `kind:"component"` with `properties` |
| `read.select` | `select:` | current selection as node entries (`kind:"gameObject"` or `kind:"asset"`); empty selection → `[]` |

Hierarchy rules: the scene must be explicit (it defines the address space —
use `core.openscene` first if needed). Reads are **one level at a time**
(ls semantics); descend by reading again. Same-name siblings make a name chain
ambiguous — `read` then fails with a candidate list, or disambiguate with an
`@instance` segment (session-stable object id, printed in every node).

### execute — the single write path

| op | args | effect |
|---|---|---|
| `execute.cs` | `code`, `imports?`, `data?` | compile + run agent-written C# with Roslyn (in memory, no domain reload) |

Contract: the code must define a static class named `Entry` with a
`public static object Main(object args)` method. `args` is the parsed `data`
JSON (cast to `Dictionary<string,object>` to read keys). The return value
becomes `result.value`, recursively converted to a JSON-serializable graph:
scalars pass through, `IDictionary`/collections recurse, `UnityEngine.Object`
becomes `{type, name, instance}`.

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
- Implementation note: `CSharpCompilation` → emit to memory →
  `Assembly.Load(byte[])` → reflection. NOT the Roslyn Scripting API — its
  assembly loader goes through `AssemblyLoadContext`, which Unity's Mono
  stubs out with `NotImplementedException`.

### log — editor console

| op | args | effect |
|---|---|---|
| `log.log` | `lines?` (default 50, max 300) | tail of the captured console ring (each entry: elapsed time + LogType) |

### core — editor-session operations (closed set)

| op | args | effect |
|---|---|---|
| `core.ping` | — | round-trip check (`result.pong` = true) |
| `core.reload` | — | `AssetDatabase.Refresh()` (import) + `RequestScriptCompilation()` (recompile, domain reload) |
| `core.status` | — | snapshot: `playing`, `paused`, `isCompiling`, `isUpdating`, `activeScene`, `openScenes[]`, `selection[]`, `projectPath`, `unityVersion`, `buildTarget` |
| `core.menuitem` | `item` | `EditorApplication.ExecuteMenuItem` (exact path, no whitelist) |
| `core.openscene` | `path`, `mode?` (`single`/`additive`) | open a scene; `.unity` suffix optional |
| `core.removescene` | `path` \| `"all"` | close a scene (discarding unsaved changes); activation moves off the target first; refuses to close the last open scene |
| `core.save` | `path?` | save all open scenes, or one scene by path |
| `core.play` / `stop` | — | enter / exit play mode |
| `core.pause` / `resume` / `step` | — | play-mode stepping |

## Security note

`execute.cs` can run arbitrary code inside the editor. The bridge binds to
nothing but the local filesystem; keep `in/` trusted. Editor-session objects
created in Edit mode (e.g. via `execute.cs`) are not persistent: they are lost
on domain reload or scene close — runtime game objects belong in Play mode.

## Agent-agnostic

The bridge speaks a plain file-queue protocol, so *any* agent with file access
can drive Unity. `skills/unity-bridge/SKILL.md` is a ready-made Agent Skill. It
is **loaded on demand**: copy or mount it into an agent only when the user asks
to control Unity — do not auto-inject it into every agent's context. The skill
never locates the bridge by a fixed path and never launches Unity; it
discovers the project under the current workspace and reports offline to the
user.
