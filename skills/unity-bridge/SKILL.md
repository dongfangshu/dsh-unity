---
name: unity-bridge
description: Drive a local Unity editor through the DSH Unity Bridge file-queue protocol v3 (read: unified read interface over assets/hierarchy/selection; execute: Roslyn C# as the single write path; log: console ring; core: editor-session ops incl. play mode, scenes, reload, status, menu items). Use when the user asks you to control, inspect, script, or test a Unity project on this machine, or when Unity is running with the bridge installed.
---

# Unity Bridge

A **file-queue protocol** that lets any agent drive a Unity editor. The Unity
side (`Assets/Plugins/UnityBridge/` inside the project) polls a command folder
and writes JSON responses — no network, no SDK, no DSH dependency. This skill
teaches the protocol so you can operate Unity directly with plain file tools.

**Prerequisite (not this skill's job):** the user has the Unity editor open
with the bridge package installed in the project. If the bridge is offline,
tell the user to open the project — never try to launch Unity yourself.

## 1. Find the bridge

The runtime queue always lives at `<unity-project>/Library/UnityBridge/`.
The bridge directory is machine-local and transient (like Unity's own cache).

Find the Unity project under your **current workspace**: a folder with an
`Assets/` directory is a Unity project (e.g. `<workspace>/UnityMain`). Ask the
user for the project path if you cannot find it, or if the workspace is not
the project itself.

```
<project>/Library/UnityBridge/
  in/       command files  <id>.json | <id>.cs  (you write here)
  running/  at most one claimed command (bridge moves here before execute)
  out/      response files <id>.json   (bridge writes here; pruned after 120s)
  done/     processed command files    (pruned after 600s)
  status/heartbeat.json                (refreshed every 1s while editor is open)
  status/log.json                      (last 300 captured console lines)
```

If `status/heartbeat.json` is missing or older than ~15 s, the bridge is
offline: the Unity editor is closed, the project doesn't have the package
installed, or the bridge is disabled (`Tools > Unity Bridge > Enable`).
**Report that to the user and ask them to open the project** — do not attempt
to start Unity.

## 2. Protocol (v3)

The capability surface is exactly **four domains**: `read | execute | log |
core`. Write a command file `in/<id>.json`:

```json
{ "id": "my-1", "domain": "read", "op": "hierarchy", "args": { "path": "Assets/Scenes/SampleScene.unity/Player" } }
```

Or drop C# directly as `in/<id>.cs` (see execute below). Response is always
`out/<id>.json`.

**Write atomically:** write `in/<id>.<ext>.tmp`, then rename to `in/<id>.<ext>`.
Do not write the final path in place — a half-written file is claimed and
fails. The bridge also ignores files younger than 150 ms as a settle window.

The bridge picks it up within ~0.2 s, **claims** it (`in/` → `running/`; at
most one in flight), executes it on the editor main thread, writes
`out/my-1.json`, then moves it to `done/`:

```json
{ "id": "my-1", "domain": "read", "op": "hierarchy", "ok": true, "ts": 1786773207.1, "result": { "path": "...", "kind": "gameObject" } }
```

Errors: `"ok": false` with `"error": "..."` (compile errors carry line numbers).

Rules:
- **Unique `id`** per command (the filename without extension); poll
  `out/<id>.json` until it appears. A response is only yours when
  `resp.id === id`.
- `domain` is required on JSON commands: `read | execute | log | core`.
- `args` is optional (`{}` when none).
- Stale files in `in/` are pruned after 10 min; `out/` after 120 s.
  `running/` is not pruned — leftovers are reaped on the next editor load.
- JSON commands and dropped `.cs` files are both UTF-8.

Helper pattern (atomic write + poll for the response):

```powershell
$id = "my-1"
$tmp = "in\$id.json.tmp"
Set-Content -Path $tmp -Value '{"id":"my-1","domain":"read","op":"select","args":{}}' -Encoding UTF8 -NoNewline
Move-Item -LiteralPath $tmp -Destination "in\$id.json"
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) { if (Test-Path "out\$id.json") { Get-Content "out\$id.json" -Raw; break }; Start-Sleep -Milliseconds 300 }
```

## 3. Ops reference

### read — the only read interface

All reads return the **same node envelope**:

```
{ "path", "kind", "name"?, "type"?, "instance"?, "activeSelf"?,
  "components"?[], "children"?[], "properties"?{}, "content"? }
```

`kind ∈ { scene, gameObject, component, text, asset }`. The envelope `path` is
always a canonical address you can read back verbatim.

| op | address | returns |
|---|---|---|
| `read.assets` | `assets:<path>` (project-relative, prefix required: `Assets/...` or `Packages/...`) | text assets (`.cs`, `.json`, ...) → `kind:"text"` with `content`; serialized assets (`.prefab`, `.asset`, `.unity`) → `kind:"asset"` with `properties` (SerializedObject dump); binary → error |
| `read.hierarchy` | `hierarchy:<scene>/<Name>/<Name>[@instance][/Type.Name]` | scene address → `kind:"scene"`, root objects as `children`; object → `kind:"gameObject"` with `children` (one level) + `components` (type names); trailing type segment → `kind:"component"` with `properties` |
| `read.select` | `select:` | current selection as entries; empty selection → `[]` |

Hierarchy rules: the scene must be explicit (open it first with
`core.openscene` if needed). Reads are **one level at a time** (ls semantics) —
descend by reading again. Same-name siblings make a name chain ambiguous:
the read fails listing candidates, or you disambiguate with an `@instance`
segment (the session-stable id printed on every node).

### execute — the only write path

Every create/update/delete is a C# script. The code must define
`public static class Entry { public static object Main(object args) { ... } }`.

**Prefer dropping the source as `in/<id>.cs`** (filename = id, body = code).
The bridge treats it as `execute.cs`; poll `out/<id>.json`. This avoids stuffing
C# into a JSON string. The JSON envelope still works unchanged:

```json
{
  "id": "cube-1",
  "domain": "execute",
  "op": "cs",
  "args": {
    "code": "public static class Entry { public static object Main(object args) { var c = GameObject.CreatePrimitive(PrimitiveType.Cube); c.name = \"agent-cube\"; c.transform.position = new Vector3(1, 2, 3); return \"created \" + c.name; } }"
  }
}
```

Dropped `.cs` file (write `in/cube-1.cs.tmp` then rename to `in/cube-1.cs`):

```csharp
public static class Entry {
  public static object Main(object args) {
    var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
    c.name = "agent-cube";
    c.transform.position = new Vector3(1, 2, 3);
    return "created " + c.name;
  }
}
```

- `args` = the parsed `data` JSON as a plain object; cast to
  `Dictionary<string, object>` to read keys. Scalars become `string` / `bool`
  / `long` / `double` / `null`.
- Default `using` directives are auto-prepended: `System`,
  `System.Collections.Generic`, `System.Linq`, `System.Text`, `System.IO`,
  `System.Threading`, `System.Text.RegularExpressions`, `UnityEngine`,
  `UnityEditor`. Extra namespaces go in `imports` (comma-separated).
- Every assembly loaded in the editor is referenced (UnityEngine, UnityEditor,
  your project scripts, ...).
- The return value becomes `result.value`, recursively converted to JSON:
  scalars pass through, dictionaries/lists recurse, `UnityEngine.Object`
  becomes `{type, name, instance}`.
- Compile errors and runtime exceptions come back in `"error"` with positions.
- Edit-mode objects are not persistent — they are lost on domain reload or
  scene close. Runtime game objects belong in Play mode.

### log — editor console

| op | args | effect |
|---|---|---|
| `log.log` | `lines?` (default 50) | tail of the captured console ring (elapsed time + LogType per entry) |

### core — editor-session operations

| op | args | effect |
|---|---|---|
| `core.ping` | — | round-trip check (`result.pong` = true) |
| `core.reload` | — | import assets + recompile all scripts (domain reload). **Send this after editing any C# file**; wait for the heartbeat to resume (10–30 s) |
| `core.status` | — | snapshot: `playing`, `paused`, `isCompiling`, `isUpdating`, `activeScene`, `openScenes[]`, `selection[]`, `projectPath`, `unityVersion`, `buildTarget` |
| `core.menuitem` | `item` | execute a menu item by exact path, e.g. `"File/Save Project"`. Missing or disabled path → `"ok": false` (validated before execute) |
| `core.openscene` | `path`, `mode?` (`single`/`additive`) | open a scene (`.unity` optional), e.g. `"Assets/Scenes/SampleScene.unity"` |
| `core.removescene` | `path` \| `"all"` | close a scene (discards unsaved changes; refuses to close the last one) |
| `core.save` | `path?` | save all open scenes, or one by path |
| `core.play` / `stop` | — | enter / exit play mode |
| `core.pause` / `resume` / `step` | — | play-mode stepping |

## 4. Typical workflows

- **Is Unity up?** → read `status/heartbeat.json` (or `core.status`).
- **Inspect the scene** → `core.status` for the active scene, then
  `read.hierarchy` on it; descend one level per read.
- **See what's in the project** → `read.assets` on the folder or file path.
- **Read an object's data** → `read.hierarchy <path>/<ComponentType>` for
  SerializedObject properties, or `read.assets` for serialized assets.
- **Create / modify / delete anything** → `execute.cs` (add components, move
  objects, spawn prefabs, `AssetDatabase.DeleteAsset`, ...).
- **Run a play test** → `core.play`, wait ~3 s, inspect via `read` /
  `log.log`, then `core.stop`.
- **After editing any C# file in the project** → send `core.reload`, wait for
  the heartbeat to resume (10–30 s), then re-check `core.status`.
- **If a read is ambiguous** → it lists candidates with `instance` ids; retry
  with the `@<instance>` segment.
- **User pasted a `unity-bridge` JSON** (Hierarchy right-click **Copy for Agent**,
  under Copy) → copied editor objects. `read.hierarchy` each `path`, or
  `read.assets` for Project-window copies. Use `@instance` if names collide.

## 5. Troubleshooting

- **`"ok": false` / timeout** — bridge offline: editor closed, package not
  installed, or `Tools > Unity Bridge` disabled. **Ask the user to open the
  project** (this skill never launches Unity).
- **No response after `core.reload`** — expected: the editor domain-reloads
  after writing `{reloading: true}`; wait for the heartbeat file to refresh.
- **`"interrupted by domain reload"`** — the command was claimed then killed
  mid-execute (script triggered a compile, or reload landed while it ran).
  It will **not** be retried; send it again if you still need the effect.
- **Compile errors in `error`** — the C# failed to compile; fix per the
  diagnostics (positions are `(line, col)`).
- **JSON parse error on a command you just wrote** — half-written file was
  claimed. Write `*.tmp` then rename; do not write the final path in place.
- **`read.hierarchy` says the scene is not open** — open it with
  `core.openscene` first (the scene defines the address space).
- **Safe Mode** — if the editor enters Safe Mode (compile errors in project
  scripts), the bridge stops; ask the user to fix the scripts and exit
  Safe Mode.

## 6. Security

`execute.cs` executes arbitrary code inside the editor (full editor API
access). Only use the bridge on projects you trust, and treat the
`Library/UnityBridge/` folder as trusted local tooling.
