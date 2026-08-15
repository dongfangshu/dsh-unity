# com.dsh.unitybridge — DSH Unity Bridge

File-queue bridge that lets a DSH agent (or anything else) drive the Unity
editor. No network ports: commands and responses travel as JSON files under
`<project>/UnityBridge/` (created automatically on editor start).

Layout (UPM package inside `Assets/Plugins/UnityBridge/`):

```
Assets/Plugins/UnityBridge/
├── package.json            # package metadata (UPM reads this)
├── download.js             # re-fetch Roslyn from nuget.org (upgrades)
├── Editor/                 # editor-only assembly
│   ├── UnityBridge.cs      # the bridge ([InitializeOnLoad], auto-start)
│   └── Roslyn/*.dll        # Microsoft.CodeAnalysis 3.8.0 + exact deps (C# 9)
└── Runtime/                # reserved for future player-side code
```

## Install (Unity 2022.3+)

**Option A — git URL (recommended).** In the target project:
`Package Manager → + → Add package from git URL`:

```
https://<host>/<user>/<repo>.git?path=/Assets/Plugins/UnityBridge
```

(`?path=` tells UPM where the package lives inside the repo.)

**Option B — manual copy.** Copy the `UnityBridge/` folder into the target
project's `Assets/Plugins/` so it reads `Assets/Plugins/UnityBridge/`.

Either way: open the project, the bridge auto-starts
(`Tools > Unity Bridge` menu). Verify a `<project>/UnityBridge/` folder appears
with `status/heartbeat.json` refreshed every second.

## Drive it (no DSH needed)

Write a command into `<project>/UnityBridge/in/`:

```json
{ "id": "demo-1", "op": "play", "args": {} }
```

Response lands in `out/demo-1.json` within ~0.2s:

```json
{ "id": "demo-1", "op": "play", "ok": true, "result": { "playing": true } }
```

## Ops

`ping` · `status` · `open_scene` (`path`, `additive?`) · `save` · `play` ·
`stop` · `pause` · `resume` · `step` · `menu` (`item`) · `eval`
(`type`, `method`, `argsJson?`) · `cs` (`code`, `imports?`, `data?`) ·
`reload` · `hierarchy` (`recursive?`) · `log` (`lines?`)

`cs` compiles agent-written C# with Roslyn in memory (no domain reload):

```csharp
using UnityEngine;
public static class Entry {
    public static object Main(object args) {
        Debug.Log("hello");
        return "done"; // becomes result.value
    }
}
```

## Security

`eval` and `cs` can execute arbitrary code inside the editor. The bridge binds
to nothing but the local filesystem — keep the `UnityBridge/` folder trusted.

## License

MIT — see `../LICENSE`.
