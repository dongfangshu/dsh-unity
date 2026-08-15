# com.dsh.unitybridge — DSH Unity Bridge (Unity side)

File-queue bridge that lets a DSH agent drive the Unity editor. No network
ports: commands and responses travel as JSON files under
`<project>/UnityBridge/` (recreated automatically on editor start).

## What's inside

```
com.dsh.unitybridge/
├── package.json
└── Editor/
    ├── UnityBridge.cs        # the bridge ([InitializeOnLoad], auto-start)
    └── Roslyn/               # Microsoft.CodeAnalysis 3.8.0 + exact runtime deps
        └── *.dll             # (12 DLLs, editor-only, C# 9 support)
```

## Install (choose one)

1. **Git URL (recommended)** — Package Manager → `+` → *Add package from git URL*:
   `https://github.com/<you>/<repo>.git#<path-to-this-folder>` (or the repo URL if
   the package sits at its root).
2. **Folder copy** — copy `com.dsh.unitybridge/` into your project's `Packages/`
   folder (embedded package).
3. **Archive** — zip it and extract into `Packages/`.

Open the project in Unity 2022.3+. The bridge auto-starts
(`Tools > Unity Bridge` menu to enable/disable). Verify: a `<project>/UnityBridge/`
folder appears with `status/heartbeat.json` refreshed every second.

## Drive it from the command line (no DSH needed)

Write a command file into `<project>/UnityBridge/in/`:

```json
{ "id": "demo-1", "op": "play", "args": {} }
```

The bridge picks it up within ~0.2s, executes it on the main thread, moves it
to `done/`, and writes the response to `out/demo-1.json`:

```json
{ "id": "demo-1", "op": "play", "ok": true, "result": { "playing": true } }
```

## Ops

`ping` · `status` · `open_scene` (`path`, `additive?`) · `save` · `play` ·
`stop` · `pause` · `resume` · `step` · `menu` (`item`) · `eval`
(`type`, `method`, `argsJson?`) · `cs` (`code`, `imports?`, `data?`) ·
`reload` · `hierarchy` (`recursive?`) · `log` (`lines?`)

`cs` compiles agent-written C# with Roslyn in memory (no domain reload).
Contract:

```csharp
using UnityEngine;
public static class Entry {
    public static object Main(object args) {
        // args = parsed `data` JSON (Dictionary<string,object> or null)
        Debug.Log("hello");
        return "done"; // becomes result.value
    }
}
```

## Security

`eval` and `cs` can execute arbitrary code inside the editor. The bridge binds
to nothing but the local filesystem — keep the `UnityBridge/` folder trusted.

## License

MIT — see `dist/LICENSE`.
