---
name: unity-bridge
description: Drive a local Unity editor through the DSH Unity Bridge file-queue protocol (status, play mode, scenes, hierarchy, static-method eval, Roslyn C# scripting, reload, logs). Use when the user asks you to control, inspect, script, or test a Unity project on this machine, or when Unity is running with the bridge installed.
---

# Unity Bridge

A **file-queue protocol** that lets any agent drive a Unity editor. The Unity
side (`Assets/Plugins/UnityBridge/`) polls a command folder and writes JSON
responses — no network, no SDK, no DSH dependency. This skill teaches the
protocol so you can operate Unity directly with plain file tools.

## 1. Locate the bridge

The bridge root is `<unity-project>/UnityBridge/`. Find the Unity project by
looking for an `Assets/` folder (Unity project marker), then use its sibling
`UnityBridge/` directory:

```
<project>/UnityBridge/
  in/     command files  <id>.json   (you write here)
  out/    response files <id>.json   (bridge writes here; pruned after 120s)
  done/   processed command files    (pruned after 600s)
  status/heartbeat.json              (refreshed every 1s while editor is open)
  status/log.json                    (last 300 captured console lines)
```

If `status/heartbeat.json` is missing or older than ~10 s, the bridge is
offline: the Unity editor is closed, the project doesn't have the package
installed, or the bridge is disabled (`Tools > Unity Bridge > Enable`).

## 2. Protocol

Write a command file `in/<id>.json`:

```json
{ "id": "my-1", "op": "play", "args": {} }
```

The bridge picks it up within ~0.2 s, executes it on the editor main thread,
moves it to `done/`, and writes `out/my-1.json`:

```json
{ "id": "my-1", "op": "play", "ok": true, "ts": 1786773207.1, "result": { "playing": true } }
```

Errors: `"ok": false` with `"error": "..."` (compile errors carry line numbers).

Rules:
- Use a **unique `id`** per command; poll `out/<id>.json` until it appears or
  ~30 s elapse. A response is only yours when `resp.id === id`.
- `args` is optional (`{}` when none).
- The bridge never cleans up `in/` on its own — it moves processed commands
  to `done/`; stale files in `in/` are pruned after 10 min.
- Everything is plain UTF-8 JSON files — any language/runtime can drive it.

Helper pattern (poll for the response):

```bash
# write the command, then:
for i in $(seq 1 100); do [ -f "out/my-1.json" ] && break; sleep 0.3; done; cat "out/my-1.json"
```

```powershell
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) { if (Test-Path "out\my-1.json") { Get-Content "out\my-1.json" -Raw; break }; Start-Sleep -Milliseconds 300 }
```

## 3. Ops reference

| op | args | effect |
|---|---|---|
| `ping` | — | round-trip check (`result.pong` = true) |
| `status` | — | snapshot: play mode, paused, open scene, root count, selection, Unity version |
| `open_scene` | `path`, `additive?` | open scene, e.g. `"Assets/Scenes/SampleScene.unity"` |
| `save` | — | save open scenes |
| `play` / `stop` | — | enter / exit play mode |
| `pause` / `resume` / `step` | — | play-mode stepping |
| `menu` | `item` | execute a menu item by exact path, e.g. `"File/Save Project"` |
| `eval` | `type`, `method`, `argsJson?` | invoke any static method (public or private); `argsJson` is a JSON array of scalars, e.g. `{"type":"System.Math","method":"Sqrt","argsJson":"[9.0]"}` |
| `cs` | `code`, `imports?`, `data?` | compile + run C# with Roslyn in memory (no domain reload) — see §4 |
| `reload` | — | recompile all scripts + domain reload in place (use after editing C# files); heartbeat pauses then resumes |
| `hierarchy` | `recursive?` | list scene root objects (`name`, `path`, `active`, `children`) |
| `log` | `lines?` | tail of captured console log (max 300) |

## 4. `cs` — run arbitrary C# in the editor (Roslyn)

Compiles with Roslyn 3.8 (C# 9) and executes in memory on the main thread.
The code must define:

```csharp
using UnityEngine;
public static class Entry {
    public static object Main(object args) {
        Debug.Log("hello");
        return "done"; // becomes result.value
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
- Compile errors and runtime exceptions come back in `"error"` with positions.

Example — create a cube:

```json
{
  "id": "cube-1",
  "op": "cs",
  "args": {
    "code": "using UnityEngine; public static class Entry { public static object Main(object args) { var c = GameObject.CreatePrimitive(PrimitiveType.Cube); c.name = \"agent-cube\"; c.transform.position = new Vector3(1, 2, 3); return \"created \" + c.name; } }"
  }
}
```

## 5. Typical workflows

- **Is Unity up?** → read `status/heartbeat.json`.
- **Run a quick play test** → `play`, wait ~3 s, read `log`/`status`, then `stop`.
- **Inspect the scene** → `hierarchy` (add `"recursive": true` for full tree).
- **After editing any C# file in the project** → send `reload`, wait for the
  heartbeat to resume (10–30 s), then re-check `status`.
- **Find something / change state** → `cs` with `GameObject.Find`,
  `Object.FindObjectsOfType`, etc.

## 6. Troubleshooting

- **`"ok": false` / timeout** — bridge offline: editor closed, package not
  installed, or `Tools > Unity Bridge` disabled. Open the project first.
- **No response after `reload`** — expected: the editor domain-reloads; wait
  for the heartbeat file to refresh.
- **Compile errors in `error`** — the C# failed to compile; fix per the
  diagnostics (positions are `(line, col)`).
- **`Ambiguous match found` on `eval`** — the target method is overloaded;
  pick a unique method name or use `cs` instead.
- **Safe Mode** — if the editor enters Safe Mode (compile errors in project
  scripts), the bridge stops; fix the scripts and reopen/exit Safe Mode.

## 7. Security

`eval` and `cs` execute arbitrary code inside the editor (full editor API
access). Only use the bridge on projects you trust, and treat the
`UnityBridge/` folder as trusted local tooling.
